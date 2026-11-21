using System.Text;
using System.Text.Json;
using LearningPortal.Core.Ai;
using LearningPortal.Core.Data;
using LearningPortal.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Services;

public sealed record ChatSummary(int Id, string Title, string? SectionLabel, int Questions, DateTime UpdatedAt);

// A section a reply used, for its source link.
public sealed record ChatSource(int SectionId, int MaterialId, string Label);

public sealed record ChatView(TopicChat Chat, string TopicName, string? SectionLabel, IReadOnlyDictionary<int, ChatSource> Sources)
{
    // The last question, when it has no reply yet (Recall is about to answer, an answer failed, or
    // the student has to choose how to go on because the material doesn't cover it).
    public ChatMessage? Unanswered => Chat.Messages.LastOrDefault() is { FromUser: true } last ? last : null;
}

// How a reply is answered. Material: the sections that fit the question (or the chat's own section).
// AllMaterial: a section chat looking in all the topic's material. Web and General: the student's
// choice when the material doesn't cover the question.
public enum ChatReplyMode { Material, AllMaterial, Web, General }

// "Ask Recall": chats with the AI about a topic. Every reply comes from the topic's material first,
// and says so when the material doesn't cover a question; a web search or the AI's own knowledge
// is used only when the student picks it. Replies stream in as the AI writes them.
public sealed class ChatService(IDbContextFactory<AppDbContext> dbFactory, SettingsService settings)
{
    // Material up to this size is sent whole (and cached, so later messages cost little); above it,
    // a short call over the outlines picks the sections for each question.
    public const int DirectTokens = 30_000;

    // The sections sent with one question, at most.
    private const int SectionTokens = 15_000;

    // Earlier messages sent along for context: the last few exchanges.
    private const int HistoryMessages = 6;

    // Web searches allowed for one reply.
    private const int MaxSearches = 3;

    public async Task<IReadOnlyList<ChatSummary>> ListAsync(string userId, int topicId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chats = await db.TopicChats.AsNoTracking()
            .Where(c => c.UserId == userId && c.TopicId == topicId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new { c.Id, c.Title, c.SectionId, c.UpdatedAt, Questions = c.Messages.Count(m => m.FromUser) })
            .ToListAsync(ct);
        var sources = chats.Any(c => c.SectionId is not null) ? await SourcesAsync(db, userId, topicId, ct) : new Dictionary<int, ChatSource>();
        return chats.Select(c => new ChatSummary(
            c.Id, c.Title, c.SectionId is { } s ? sources.GetValueOrDefault(s)?.Label : null, c.Questions, c.UpdatedAt)).ToList();
    }

    public async Task<ChatView> GetAsync(string userId, int chatId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chat = await db.TopicChats.AsNoTracking().Include(c => c.Topic).Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == chatId && c.UserId == userId, ct) ?? throw new NotFoundException();
        chat.Messages = chat.Messages.OrderBy(m => m.Order).ToList();
        var sources = await SourcesAsync(db, userId, chat.TopicId, ct);
        var label = chat.SectionId is { } s ? sources.GetValueOrDefault(s)?.Label : null;
        return new ChatView(chat, chat.Topic!.Name, label, sources);
    }

    // The name of a section of this topic, for "Ask about this section". Throws NotFoundException
    // when it isn't one.
    public async Task<ChatSource> SectionAsync(string userId, int topicId, int sectionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await SourcesAsync(db, userId, topicId, ct)).GetValueOrDefault(sectionId) ?? throw new NotFoundException();
    }

    // Starts a chat with its first question; Recall's reply follows with ReplyAsync.
    public async Task<int> StartAsync(string userId, int topicId, int? sectionId, string question, CancellationToken ct = default)
    {
        question = CheckQuestion(question);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Topics.AnyAsync(t => t.Id == topicId && t.UserId == userId, ct))
            throw new NotFoundException();
        if (sectionId is { } sid && !await db.MaterialSections.AnyAsync(s => s.Id == sid && s.Material!.TopicId == topicId && s.Material.UserId == userId, ct))
            throw new NotFoundException();

        var chat = new TopicChat { UserId = userId, TopicId = topicId, SectionId = sectionId, Title = TitleFrom(question) };
        chat.Messages.Add(new ChatMessage { Order = 0, FromUser = true, Text = question });
        db.TopicChats.Add(chat);
        await db.SaveChangesAsync(ct);
        return chat.Id;
    }

    // Adds the student's next question; Recall's reply follows with ReplyAsync.
    public async Task AskAsync(string userId, int chatId, string question, CancellationToken ct = default)
    {
        question = CheckQuestion(question);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chat = await db.TopicChats.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == chatId && c.UserId == userId, ct)
            ?? throw new NotFoundException();
        var last = chat.Messages.OrderBy(m => m.Order).LastOrDefault();
        if (last is { FromUser: true })
            throw new InvalidOperationException("Wait for Recall's reply to your last question first.");
        if (chat.Messages.Count >= TopicChat.MaxMessages)
            throw new InvalidOperationException("This chat is full. Start a new chat to ask more.");

        chat.Messages.Add(new ChatMessage { Order = (last?.Order ?? -1) + 1, FromUser = true, Text = question });
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Answers the chat's last question. onText gets the reply's text so far each time more arrives.
    // When the material doesn't cover the question, nothing is answered: the question is marked so
    // the student can choose how to go on.
    public async Task ReplyAsync(
        string userId, int chatId, ChatReplyMode mode, AiChoice? choice, Action<string> onText, IProgress<string>? progress, CancellationToken ct)
    {
        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var client = AiClientFactory.Create(connection);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chat = await db.TopicChats.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == chatId && c.UserId == userId, ct)
            ?? throw new NotFoundException();
        var messages = chat.Messages.OrderBy(m => m.Order).ToList();
        if (messages.LastOrDefault() is not { FromUser: true } question)
            return;

        var material = await SourceFinder.LoadAsync(db, userId, chat.TopicId, ct);
        var topic = material.Topic;
        var earlier = messages.Take(messages.Count - 1).TakeLast(HistoryMessages).Select(m => (m.FromUser, m.Text)).ToList();
        var prompt = Prompts.ChatPrompt(earlier, question.Text);
        var reply = new ChatMessage { ChatId = chat.Id, Order = question.Order + 1, FromUser = false, Model = connection.Model };

        switch (mode)
        {
            case ChatReplyMode.Material or ChatReplyMode.AllMaterial:
            {
                var pinned = chat.SectionId is { } sid ? material.Section(sid) : null;
                List<MaterialSection> sections;
                if (pinned is not null && mode == ChatReplyMode.Material)
                {
                    sections = [pinned];
                }
                else
                {
                    progress?.Report("Recall is looking through your material...");
                    var previous = messages.Take(messages.Count - 1).LastOrDefault(m => m.FromUser)?.Text;
                    var locate = previous is null ? question.Text : $"Earlier question: {previous}\nLatest question: {question.Text}";
                    sections = await SourceFinder.PickAsync(client, material, [locate], DirectTokens, SectionTokens, ct);
                }
                if (sections.Count == 0)
                {
                    await MarkNotCoveredAsync(db, question);
                    return;
                }

                progress?.Report("Recall is answering...");
                var text = new StringBuilder();
                var state = ChatReplyState.Parse("");
                await foreach (var piece in client.StreamTextAsync(new AiTextRequest
                {
                    System = Prompts.ChatSystem(topic.Name, topic.Language, Prompts.AnswerBasis.Material, pinned is not null && mode == ChatReplyMode.Material),
                    Context = material.Context(sections),
                    Prompt = prompt,
                    CacheKey = $"topic-{topic.Id}",
                }, ct))
                {
                    text.Append(piece);
                    state = ChatReplyState.Parse(text.ToString());
                    if (state is { Decided: true, NotCovered: false })
                        onText(state.Visible);
                }
                if (state.NotCovered)
                {
                    await MarkNotCoveredAsync(db, question);
                    return;
                }
                var sent = sections.Select(s => s.Id).ToHashSet();
                reply.Text = state.Visible;
                reply.AnswerSource = AnswerSource.Material;
                reply.SectionIds = string.Join(',', state.SectionIds.Where(sent.Contains));
                if (reply.SectionIds.Length == 0)
                    reply.SectionIds = null;
                break;
            }
            case ChatReplyMode.General:
            {
                progress?.Report("Recall is answering...");
                var text = new StringBuilder();
                await foreach (var piece in client.StreamTextAsync(new AiTextRequest
                {
                    System = Prompts.ChatSystem(topic.Name, topic.Language, Prompts.AnswerBasis.General, false),
                    Prompt = prompt,
                }, ct))
                {
                    text.Append(piece);
                    onText(text.ToString().Trim());
                }
                reply.Text = text.ToString().Trim();
                reply.AnswerSource = AnswerSource.General;
                break;
            }
            case ChatReplyMode.Web:
            {
                progress?.Report("Recall is searching the web...");
                var research = await client.ResearchAsync(new WebResearchRequest
                {
                    System = Prompts.ChatSystem(topic.Name, topic.Language, Prompts.AnswerBasis.Research, false),
                    Prompt = prompt,
                    MaxSearches = MaxSearches,
                    MaxTokens = 6_000,
                }, ct);
                reply.Text = research.Text.Trim();
                reply.AnswerSource = AnswerSource.Web;
                reply.WebSourcesJson = WebSource.ToJson(research.Citations.DistinctBy(c => c.Url).Take(8).Select(c => new WebSource(c.Title, c.Url)));
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(reply.Text))
            throw new AiException("The AI returned an empty reply. Try again.");

        question.NotCovered = false;
        chat.Messages.Add(reply);
        chat.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    // Turns a reply, with the question it answers, into a written question in the topic's bank and
    // adds it to an exam: the one given, or a new exam with this name. Returns the exam's id.
    public async Task<int> SaveToExamAsync(
        string userId, int messageId, int? examId, string? newExamName, AiChoice? choice, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reply = await db.ChatMessages.Include(m => m.Chat)
            .FirstOrDefaultAsync(m => m.Id == messageId && !m.FromUser && m.Chat!.UserId == userId, ct) ?? throw new NotFoundException();
        var chat = reply.Chat!;

        if (reply.SavedQuestionId is { } saved && await db.Questions.FirstOrDefaultAsync(q => q.Id == saved, ct) is { } existing)
        {
            var target = await ExamTargets.ResolveAsync(db, userId, chat.TopicId, examId, newExamName, existing.Difficulty, ct);
            ExamTargets.Add(target, existing);
            await db.SaveChangesAsync(ct);
            return target.Id;
        }

        var asked = await db.ChatMessages.Where(m => m.ChatId == chat.Id && m.FromUser && m.Order < reply.Order)
            .OrderByDescending(m => m.Order).Select(m => m.Text).FirstOrDefaultAsync(ct) ?? "";
        var topic = await db.Topics.AsNoTracking().FirstAsync(t => t.Id == chat.TopicId, ct);
        // Before the AI call, so a wrong exam costs nothing; a new exam takes the question's difficulty below.
        var exam = await ExamTargets.ResolveAsync(db, userId, chat.TopicId, examId, newExamName, Difficulty.Medium, ct);

        var connection = await settings.GetConnectionAsync(userId, AiTask.Generation, choice, ct);
        var json = await AiClientFactory.Create(connection).CompleteJsonAsync(new AiRequest
        {
            System = Prompts.ChatQuestionSystem(topic.Language, topic.IsProgramming),
            Prompt = Prompts.ChatQuestionPrompt(asked, reply.Text),
            SchemaName = "chat_question",
            Schema = Prompts.ChatQuestionSchema,
            MaxTokens = 6_000,
        }, ct);

        var question = new Question
        {
            TopicId = chat.TopicId, UserId = userId, Type = QuestionType.Written, GeneratedByModel = connection.Model,
            AnswerSource = reply.AnswerSource ?? AnswerSource.Material, WebSourcesJson = reply.WebSourcesJson,
        };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            question.Prompt = r.GetProperty("prompt").GetString()?.Trim() ?? "";
            question.ReferenceAnswer = r.GetProperty("reference_answer").GetString()?.Trim() ?? "";
            question.Explanation = r.GetProperty("explanation").GetString()?.Trim() ?? "";
            question.IsCode = topic.IsProgramming && r.GetProperty("kind").GetString() == "code";
            question.Difficulty = r.GetProperty("difficulty").GetString() switch { "easy" => Difficulty.Easy, "hard" => Difficulty.Hard, _ => Difficulty.Medium };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiException("The AI's answer couldn't be read. Try again.");
        }
        if (question.Prompt.Length == 0 || question.ReferenceAnswer.Length == 0)
            throw new AiException("The AI couldn't make a question from this reply. Try another one.");

        // The question's source: the first section the reply used. A web reply's text stands in for
        // the section when the answer is graded or asked about, as the notes of other web answers do.
        if (reply.SectionIdList.FirstOrDefault() is var sectionId and > 0
            && (await SourcesAsync(db, userId, chat.TopicId, ct)).GetValueOrDefault(sectionId) is { } source)
        {
            question.SourceSectionId = source.SectionId;
            question.SourceMaterialId = source.MaterialId;
            question.SourceLabel = source.Label;
        }
        if (reply.AnswerSource == AnswerSource.Web)
            question.SourceNote = reply.Text.Length > 3_000 ? reply.Text[..3_000] : reply.Text;

        db.Questions.Add(question);
        if (exam.Id == 0)
            exam.Difficulty = question.Difficulty;
        ExamTargets.Add(exam, question);
        await db.SaveChangesAsync(ct);

        reply.SavedQuestionId = question.Id;
        await db.SaveChangesAsync(CancellationToken.None);
        return exam.Id;
    }

    public async Task RenameAsync(string userId, int chatId, string title, CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0)
            throw new ArgumentException("Give the chat a name.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var chat = await db.TopicChats.FirstOrDefaultAsync(c => c.Id == chatId && c.UserId == userId, ct) ?? throw new NotFoundException();
        chat.Title = title.Length > TopicChat.MaxTitleLength ? title[..TopicChat.MaxTitleLength] : title;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int chatId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.TopicChats.Where(c => c.Id == chatId && c.UserId == userId).ExecuteDeleteAsync(ct);
    }

    // Upper bound for a reply from a web search: its searches and their cost (null without a price).
    public async Task<(int Searches, decimal? Cost, AiModelInfo? Model)> ResearchCostAsync(string userId, AiChoice? choice, CancellationToken ct = default)
    {
        var model = choice is not null ? AiModels.Find(choice) : await settings.GetPricedModelAsync(userId, AiTask.Generation, ct);
        // Search results come back as input, a few thousand tokens each, plus the chat and the reply.
        var cost = model?.Cost(new AiUsage(MaxSearches * 4_000 + 3_000, 0, 0, 2_000, MaxSearches));
        return (MaxSearches, cost, model);
    }

    // About how many tokens one question sends: the material (all of it when it's small, else the
    // outlines for picking plus the picked sections, or just the chat's section), plus the chat.
    // Follow-ups on a small topic read the material back from the cache for much less.
    public async Task<int> QuestionTokensAsync(string userId, int topicId, int? sectionId, CancellationToken ct = default)
    {
        const int chat = 2_500;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (sectionId is { } sid)
        {
            var section = await db.MaterialSections.Where(s => s.Id == sid && s.Material!.UserId == userId)
                .Select(s => (int?)s.TokenEstimate).FirstOrDefaultAsync(ct);
            if (section is { } tokens)
                return tokens + chat;
        }
        var sizes = await db.Materials.Where(m => m.TopicId == topicId && m.UserId == userId)
            .Select(m => new { m.TokenEstimate, OutlineLength = m.Outline == null ? 0 : m.Outline.Length })
            .ToListAsync(ct);
        var total = sizes.Sum(s => s.TokenEstimate);
        return total <= DirectTokens ? total + chat : SectionTokens + sizes.Sum(s => s.OutlineLength) / 4 + chat;
    }

    private static async Task MarkNotCoveredAsync(AppDbContext db, ChatMessage question)
    {
        question.NotCovered = true;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static string CheckQuestion(string question)
    {
        question = question.Trim();
        if (question.Length == 0)
            throw new ArgumentException("Type your question first.");
        if (question.Length > ChatMessage.MaxQuestionLength)
            throw new ArgumentException($"Keep the question under {ChatMessage.MaxQuestionLength:N0} characters.");
        return question;
    }

    // A chat is named after its first question, shortened to one line.
    private static string TitleFrom(string question)
    {
        var line = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int max = 80;
        if (line.Length <= max)
            return line;
        // Cut at a word break when there's one in the second half.
        var cut = line.LastIndexOf(' ', max - 1);
        return line[..(cut > max / 2 ? cut : max)].TrimEnd() + "...";
    }

    // Every section of the topic with its material and name, without the material's text.
    private static async Task<Dictionary<int, ChatSource>> SourcesAsync(AppDbContext db, string userId, int topicId, CancellationToken ct)
    {
        var rows = await db.Materials.AsNoTracking()
            .Where(m => m.TopicId == topicId && m.UserId == userId)
            .Select(m => new { m.Id, m.Title, m.Outline, Sections = m.Sections.Select(s => new { s.Id, s.Index, s.Heading }).ToList() })
            .ToListAsync(ct);
        var sources = new Dictionary<int, ChatSource>();
        foreach (var row in rows)
        {
            var material = new Material { Id = row.Id, Title = row.Title, Outline = row.Outline };
            material.Sections = row.Sections.OrderBy(s => s.Index)
                .Select(s => new MaterialSection { Id = s.Id, Index = s.Index, Heading = s.Heading, MaterialId = row.Id }).ToList();
            foreach (var section in material.Sections)
                sources[section.Id] = new ChatSource(section.Id, row.Id, QuestionGenerationService.SourceLabel(material, section));
        }
        return sources;
    }
}
