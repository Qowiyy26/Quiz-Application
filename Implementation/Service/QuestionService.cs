using Quiz_Application.Interface.Repository;
using Quiz_Application.Interface.Service;
using Quiz_Application.Interfaces.Repositories;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO.Question;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Quiz_Application.Models.DTO;

namespace Quiz_Application.Implementation.Service
{
    public class QuestionService : IQuestionService
    {
        private readonly IQuestionRepository _questionRepository;
        private readonly ILanguageRepository _languageRepository;
        private readonly HttpClient _httpClient;
        private readonly ILogger<QuestionService> _logger;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly string _geminiApiKey;

        public QuestionService(
            IQuestionRepository questionRepository,
            IUnitOfWork unitOfWork,
            ILanguageRepository languageRepository,
            HttpClient httpClient,
            ILogger<QuestionService> logger,
            IConfiguration config,
            IMemoryCache cache)
        {
            _questionRepository = questionRepository;
            _languageRepository = languageRepository;
            _httpClient = httpClient;
            _unitOfWork = unitOfWork;
            _logger = logger;
            _config = config;
            _cache = cache;
            _geminiApiKey = _config["Gemini:ApiKey"] ?? string.Empty;
        }

        public async Task<List<QuestionDTO>> GenerateQuestionsFromApiAsync(Guid languageId, string level, int numberOfQuestions, Guid userId, IEnumerable<string>? selectedSubtopics, string? subtopicTrack, CancellationToken cancellation)
        {
            var language = await _languageRepository.GetLanguageByIdAsync(languageId);
            if (language == null) throw new Exception("Invalid language");

            numberOfQuestions = Math.Clamp(numberOfQuestions, 20, 150);
            var languageName = language.LanguageName ?? "Technology";
            var allowedSubtopics = TechSubtopicCatalog.GetSubtopics(languageName, subtopicTrack);
            var selected = (selectedSubtopics ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Where(s => allowedSubtopics.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var focusAreas = selected.Any() ? selected : allowedSubtopics;
            var previousQuestions = (await _questionRepository.GetPreviousQuestionTextsAsync(userId, languageId))
                .Select(NormalizeQuestion)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(_geminiApiKey))
            {
                return BuildFallbackQuestions(languageId, languageName, level, numberOfQuestions, previousQuestions, focusAreas);
            }

            var questions = new List<QuestionDTO>();
            var batchSize = Math.Clamp(_config.GetValue<int?>("QuizGeneration:BatchSize") ?? 50, 20, 75);

            while (questions.Count < numberOfQuestions)
            {
                var remaining = numberOfQuestions - questions.Count;
                var currentBatchSize = Math.Min(batchSize, remaining);
                var prompt = BuildPrompt(languageName, level, currentBatchSize, questions.Count + 1, previousQuestions, focusAreas, subtopicTrack);

                var requestBody = new
                {
                    contents = new[]
                    {
                        new {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.82,
                        topP = 0.95,
                        topK = 40,
                        maxOutputTokens = 16384
                    }
                };

                try
                {
                    var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={_geminiApiKey}");
                    request.Content = content;

                    var response = await _httpClient.SendAsync(request, cancellation);
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync(cancellation);
                        _logger.LogWarning("Gemini question generation failed. Status: {StatusCode}. Content: {Content}", response.StatusCode, errorContent);
                        break;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellation);
                    using var doc = JsonDocument.Parse(json);
                    var geminiContent = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                    var cleanedJson = Regex.Replace(geminiContent ?? "[]", @"^```json|```$", "", RegexOptions.Multiline).Trim();
                    var rawQuestions = JsonSerializer.Deserialize<List<ExternalQuestionModel>>(cleanedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var currentQuestions = questions.Select(q => NormalizeQuestion(q.QuestionText)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var blockedQuestions = previousQuestions.Concat(currentQuestions).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var batchQuestions = MapQuestions(rawQuestions, languageId, level, currentBatchSize, blockedQuestions);

                    if (!batchQuestions.Any())
                    {
                        break;
                    }

                    questions.AddRange(batchQuestions);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Using fallback question fill for language {LanguageId}", languageId);
                    break;
                }
            }

            if (questions.Count < numberOfQuestions)
            {
                var blockedQuestions = previousQuestions
                    .Concat(questions.Select(q => NormalizeQuestion(q.QuestionText)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fallback = BuildFallbackQuestions(languageId, languageName, level, numberOfQuestions - questions.Count, blockedQuestions, focusAreas);
                questions.AddRange(fallback);
            }

            questions = questions.Take(numberOfQuestions).ToList();
            _cache.Set($"QuizQuestions_{languageId}", questions, TimeSpan.FromMinutes(30));
            return questions;
        }

        public async Task<bool> SaveQuestionsAsync(Guid quizId, List<QuestionDTO> questions, CancellationToken cancellation)
        {
            if (questions == null || !questions.Any()) return false;

            var entities = questions.Select(dto => new Question
            {
                Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
                QuestionText = dto.QuestionText,
                OptionA = dto.OptionA,
                OptionB = dto.OptionB,
                OptionC = dto.OptionC,
                OptionD = dto.OptionD,
                CorrectOption = dto.CorrectAnswer,
                QuizId = quizId,
                LanguageId = dto.LanguageId,
                Difficulty = dto.Difficulty
            }).ToList();

            await _questionRepository.AddQuestionsAsync(entities);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<IEnumerable<QuestionDTO>> GetQuestionsByQuizIdAsync(Guid quizId, CancellationToken cancellation)
        {
            var list = await _questionRepository.GetQuestionsByQuizIdAsync(quizId);
            return list.Select(q => new QuestionDTO
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                OptionA = q.OptionA,
                OptionB = q.OptionB,
                OptionC = q.OptionC,
                OptionD = q.OptionD,
                CorrectAnswer = q.CorrectOption,
                QuizId = q.QuizId,
                LanguageId = q.LanguageId,
                Difficulty = q.Difficulty
            });
        }

        public async Task<QuestionDTO?> GetQuestionByIdAsync(Guid questionId, CancellationToken cancellation)
        {
            var q = await _questionRepository.GetQuestionByIdAsync(questionId);
            if (q == null) return null;

            return new QuestionDTO
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                OptionA = q.OptionA,
                OptionB = q.OptionB,
                OptionC = q.OptionC,
                OptionD = q.OptionD,
                CorrectAnswer = q.CorrectOption,
                QuizId = q.QuizId,
                LanguageId = q.LanguageId,
                Difficulty = q.Difficulty
            };
        }

        public async Task<bool> DeleteQuestionAsync(Guid questionId, CancellationToken cancellation)
        {
            var q = await _questionRepository.GetQuestionByIdAsync(questionId);
            if (q == null) return false;

            await _questionRepository.DeleteAsync(q);
            await _unitOfWork.SaveChangesAsync();
            return true;
        }

        public async Task<IEnumerable<QuestionDTO>> GetQuestionsForUserAsync(int requestedCount, CancellationToken cancellation)
        {
            var questions = await _questionRepository.GetRandomQuestionsAsync(requestedCount);

            return questions.Select(q => new QuestionDTO
            {
                Id = q.Id,
                QuestionText = q.QuestionText,
                OptionA = q.OptionA,
                OptionB = q.OptionB,
                OptionC = q.OptionC,
                OptionD = q.OptionD,
                CorrectAnswer = q.CorrectOption,
                QuizId = q.QuizId,
                LanguageId = q.LanguageId,
                Difficulty = q.Difficulty
            });
        }



        private static string BuildPrompt(string languageName, string level, int count, int startNumber, HashSet<string> previousQuestions, List<string> focusAreas, string? subtopicTrack)
        {
            var exclusions = previousQuestions.Any()
                ? "Do not repeat or closely paraphrase these previous questions: " + string.Join(" | ", previousQuestions.Take(40)) + ". "
                : string.Empty;
            var difficultyInstruction = GetDifficultyInstruction(level);
            var focusText = string.Join(", ", focusAreas);
            var trackInstruction = GetTrackInstruction(subtopicTrack);

            return $"Generate exactly {count} difficult multiple-choice quiz questions for the selected technology topic: {languageName}. " +
                   $"The questions must be about {languageName} only, not about a general course category. " +
                   $"Use only these selected subtopics/focus areas as the question source: {focusText}. " +
                   exclusions +
                   $"{trackInstruction} " +
                   $"Make at least 95 percent of the questions practical, scenario-based, and code-focused. Only 5 percent may be pure theory. Put the learner inside real-life software tasks such as building a login flow, fixing a bug, completing a controller/action, choosing a SQL query, securing an endpoint, debugging an API request, refactoring a method, reading a stack trace, writing a method body, or completing a small code snippet. " +
                   $"Most questions must include a short code snippet, command, query, method signature, configuration sample, terminal output, stack trace, or pseudo-code scenario inside the question text. " +
                   $"Ask what code should replace a TODO, which method call completes the feature, which snippet produces the required behavior, which implementation is safest, why a method is used, what bug causes the output, and which tradeoff matters in a real project. " +
                   $"Do not ask shallow questions like 'What is X?' unless it is one of the rare theory questions. A question is too easy if a learner can answer it by recognizing a keyword without reading the scenario or code. " +
                   $"Every practical question must require code reasoning: the learner should inspect the snippet, understand the method or behavior, and choose the best action, code line, query, command, fix, or design decision. " +
                   $"The correct answer should be discoverable by careful reading of the code and requirement, but not obvious from wording alone. " +
                   $"The small theory portion may ask about purpose, advantages, disadvantages, usefulness, limitations, syntax, commands, or best practices, but it must still connect to the selected subtopic and include a realistic use case. " +
                   $"Difficulty level: {level}. {difficultyInstruction} " +
                   $"Keep the difficulty strict: Beginner must not use professional architecture questions; Intermediate must be more applied than Beginner; Professional must include workplace code decisions and production tradeoffs; Advanced must be the hardest with complex edge cases and deep reasoning. " +
                   $"Make the options challenging and educational: every question must have exactly one correct answer. " +
                   $"At least three options must look technically possible, and at least two wrong options must be close enough to confuse a serious learner. " +
                   $"The wrong options should use common real mistakes: near-correct syntax, missing await/return/null checks, unsafe validation, wrong lifecycle, wrong SQL filtering, hidden performance problems, weak security, reversed concepts, or incomplete best practices. " +
                   $"Do not make wrong options silly, unrelated, impossible, or obviously false. Avoid options like 'delete everything', 'ignore all errors', or 'it always works'. " +
                   $"The correct answer must appear in a random position among the four options, not always first. " +
                   $"Start from question number {startNumber}, but do not include numbering in the JSON. " +
                   $"Each question must have 4 options. " +
                   $"The correctAnswer must exactly match the full text of one option. " +
                   $"Return valid JSON only as an array of objects with this shape: " +
                   $"[{{\"question\":\"string\",\"options\":[\"A\",\"B\",\"C\",\"D\"],\"correctAnswer\":\"string\"}}]. " +
                   $"Do not use markdown, explanations, comments, or trailing commas.";
        }

        private static List<QuestionDTO> MapQuestions(List<ExternalQuestionModel>? rawQuestions, Guid languageId, string level, int count, HashSet<string> blockedQuestions)
        {
            if (rawQuestions == null) return new List<QuestionDTO>();

            return rawQuestions
                .Where(q => !string.IsNullOrWhiteSpace(q.Question) && q.Options.Count >= 4)
                .Where(q => !blockedQuestions.Contains(NormalizeQuestion(q.Question)))
                .Take(count)
                .Select(q =>
                {
                    var options = q.Options.Take(4).Select(o => o ?? string.Empty).ToList();
                    var correctAnswer = options.FirstOrDefault(o => string.Equals(o, q.CorrectAnswer, StringComparison.OrdinalIgnoreCase))
                        ?? options.First();
                    options = ShuffleOptions(options);

                    return new QuestionDTO
                    {
                        Id = Guid.NewGuid(),
                        QuestionText = q.Question,
                        OptionA = options.ElementAtOrDefault(0) ?? string.Empty,
                        OptionB = options.ElementAtOrDefault(1) ?? string.Empty,
                        OptionC = options.ElementAtOrDefault(2) ?? string.Empty,
                        OptionD = options.ElementAtOrDefault(3) ?? string.Empty,
                        CorrectAnswer = correctAnswer,
                        LanguageId = languageId,
                        Difficulty = level
                    };
                })
                .ToList();
        }

        private class ExternalQuestionModel
        {
            public string Question { get; set; } = "";
            public List<string> Options { get; set; } = new();
            public string CorrectAnswer { get; set; } = "";
        }

        private static List<QuestionDTO> BuildFallbackQuestions(Guid languageId, string languageName, string level, int count, HashSet<string> blockedQuestions, List<string> focusAreas)
        {
            var templates = new[]
            {
                new { Question = "A {0} service method using {2} must save a record only when validation passes:\n\nif (!IsValid(input)) return Error(\"Invalid\");\n// TODO\nreturn Success();\n\nWhich replacement best fits a real production method?", A = "await repository.SaveAsync(input);", B = "repository.SaveAsync(input);", C = "return Success(); await repository.SaveAsync(input);", D = "if (input != null) return Success();", Correct = "await repository.SaveAsync(input);" },
                new { Question = "A {0} feature using {2} fails only when the requested item does not exist:\n\nvar item = await repo.FindAsync(id);\nreturn item.Name;\n\nWhat is the most correct fix?", A = "Check item for null and return a clear not-found result before reading Name", B = "Call item.Name twice so the value is loaded", C = "Convert id to string before calling FindAsync", D = "Wrap return item.Name in a loop until it succeeds", Correct = "Check item for null and return a clear not-found result before reading Name" },
                new { Question = "A developer reviews this {0} logic around {2}:\n\nif (user.Role == \"Admin\" || user.Id == ownerId)\n    DeleteRecord(id);\n\nWhy is this method used before delete?", A = "It enforces authorization so only admins or owners can perform the action", B = "It speeds up database indexing before delete", C = "It converts every user into an admin for this request", D = "It prevents the compiler from checking the method", Correct = "It enforces authorization so only admins or owners can perform the action" },
                new { Question = "A {0} API endpoint using {2} returns duplicate rows after a join. Which fix is most appropriate when the requirement is one record per user?", A = "Group or filter by the user key and return the required projection only", B = "Increase the HTTP timeout so duplicates disappear", C = "Sort by name because sorting removes duplicates", D = "Move the query to the controller without changing it", Correct = "Group or filter by the user key and return the required projection only" },
                new { Question = "A {0} method using {2} is slow:\n\nforeach (var id in ids)\n{\n    var item = await repo.FindAsync(id);\n    results.Add(item);\n}\n\nWhat is the best production fix?", A = "Fetch the required records in one query/batch instead of one query per id", B = "Add more comments inside the loop", C = "Convert ids to uppercase before the loop", D = "Run the same loop twice to warm up the database", Correct = "Fetch the required records in one query/batch instead of one query per id" },
                new { Question = "A {0} form using {2} accepts this client value:\n\nprice = \"-500\"\n\nWhich server-side check is most important before saving?", A = "Validate type, range, and business rule on the server even if the UI already validates", B = "Trust the browser validation because users cannot change form data", C = "Save the value first and validate it in the dashboard later", D = "Hide the input field so invalid values cannot exist", Correct = "Validate type, range, and business rule on the server even if the UI already validates" },
                new { Question = "A {0} controller action using {2} catches every exception and returns success:\n\ntry {{ await SaveAsync(model); }} catch {{ }}\nreturn Ok();\n\nWhat is the hardest bug this creates?", A = "The app hides failed saves, so users and logs can show success when data was lost", B = "The app compiles slower because catch blocks are expensive", C = "The method automatically retries forever", D = "The database creates extra tables for each exception", Correct = "The app hides failed saves, so users and logs can show success when data was lost" },
                new { Question = "A teammate proposes this {0} fix for {2}:\n\nvar sql = \"SELECT * FROM Users WHERE Email = '\" + email + \"'\";\n\nWhich replacement is safest?", A = "Use a parameterized query or ORM filter that binds email as a parameter", B = "Remove spaces from email before concatenating it", C = "Put the SQL string in a different class", D = "Add ORDER BY Email to make injection impossible", Correct = "Use a parameterized query or ORM filter that binds email as a parameter" },
                new { Question = "A {0} method using {2} must update an entity and preserve audit fields. Which implementation is best?", A = "Load the existing entity, update allowed fields, validate, then save changes", B = "Create a new entity from the request and overwrite every column", C = "Accept all request fields because the client already knows the model", D = "Delete the old row and insert a new row for every edit", Correct = "Load the existing entity, update allowed fields, validate, then save changes" },
                new { Question = "A {0} deployment using {2} works locally but fails in production because it reads localhost configuration. What is the correct fix?", A = "Read production settings from environment variables and keep localhost only for development", B = "Hard-code the developer machine password in the source code", C = "Rename the database so localhost becomes public", D = "Disable migrations so connection errors are invisible", Correct = "Read production settings from environment variables and keep localhost only for development" }
            };

            var questions = new List<QuestionDTO>();
            var attempt = 0;

            while (questions.Count < count && attempt < count * 4)
            {
                var template = templates[attempt % templates.Length];
                var focusArea = focusAreas[attempt % focusAreas.Count];
                var questionText = string.Format(template.Question, languageName, level, focusArea);
                if (attempt >= templates.Length)
                {
                    questionText = $"{questionText} Scenario {attempt / templates.Length + 1}.";
                }

                var normalized = NormalizeQuestion(questionText);
                attempt++;
                if (blockedQuestions.Contains(normalized))
                {
                    continue;
                }

                blockedQuestions.Add(normalized);
                var options = ShuffleOptions(new List<string> { template.A, template.B, template.C, template.D });

                questions.Add(new QuestionDTO
                {
                    Id = Guid.NewGuid(),
                    QuestionText = questionText,
                    OptionA = options.ElementAtOrDefault(0) ?? string.Empty,
                    OptionB = options.ElementAtOrDefault(1) ?? string.Empty,
                    OptionC = options.ElementAtOrDefault(2) ?? string.Empty,
                    OptionD = options.ElementAtOrDefault(3) ?? string.Empty,
                    CorrectAnswer = template.Correct,
                    LanguageId = languageId,
                    Difficulty = level
                });
            }

            return questions;
        }

        private static List<string> ShuffleOptions(List<string> options)
        {
            return options
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
        }

        private static string GetDifficultyInstruction(string level)
        {
            return level?.Trim().ToLowerInvariant() switch
            {
                "beginner" => "Ask foundation questions, but make them practical and code-based. Use short snippets with one hidden mistake, simple method/function behavior, syntax completion, input validation, advantages, disadvantages, and common beginner mistakes. The answer should require reading the code, not memorizing a definition.",
                "intermediate" => "Ask applied questions that require two-step reasoning. Use project scenarios, debugging, data flow, method selection, implementation choices, small code traces, edge cases, advantages, and disadvantages. Make every question clearly harder than Beginner.",
                "professional" => "Ask difficult workplace-level questions. Include realistic code snippets, design decisions, security, maintainability, testing, integration, performance tradeoffs, production debugging, and choosing the best method or pattern for a real project. Avoid definition-only questions.",
                "pro" => "Ask difficult workplace-level questions. Include realistic code snippets, design decisions, security, maintainability, testing, integration, performance tradeoffs, production debugging, and choosing the best method or pattern for a real project. Avoid definition-only questions.",
                "advanced" => "Ask very hard questions. Use advanced code behavior, optimization, internals, concurrency, scalability, edge cases, limitations, tradeoffs, failure modes, architecture reasoning, and expert-level scenarios. The learner should need to inspect the code carefully.",
                "expert" => "Ask very hard questions. Use deep internals, advantages, disadvantages, architecture tradeoffs, failure modes, performance, security, concurrency, and scenario-based problem solving. The learner should need to inspect the code carefully.",
                _ => "Match the requested skill level and keep the questions practical."
            };
        }

        private static string GetTrackInstruction(string? subtopicTrack)
        {
            return subtopicTrack?.Trim().ToLowerInvariant() switch
            {
                "fundamentals" => "Use beginner-friendly subtopics only: core concepts, syntax, simple functions, basic usage, and first-level mistakes.",
                "professional" => "Use professional subtopics: debugging, security, testing, performance, integration, database/API work, deployment concerns, and maintainable project code.",
                "architecture" => "Use the full architecture view: combine fundamentals, implementation, integration, security, performance, and system design subtopics under the selected course.",
                _ => "Use the selected subtopics exactly as the source for the questions."
            };
        }

        private static string NormalizeQuestion(string? question)
        {
            if (string.IsNullOrWhiteSpace(question)) return string.Empty;
            var lower = question.Trim().ToLowerInvariant();
            return Regex.Replace(lower, @"\s+", " ");
        }
    }
}
