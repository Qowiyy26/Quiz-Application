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

        public async Task<List<QuestionDTO>> GenerateQuestionsFromApiAsync(Guid languageId, string level, int numberOfQuestions, Guid userId, IEnumerable<string>? selectedSubtopics, CancellationToken cancellation)
        {
            var language = await _languageRepository.GetLanguageByIdAsync(languageId);
            if (language == null) throw new Exception("Invalid language");

            numberOfQuestions = Math.Clamp(numberOfQuestions, 20, 150);
            var languageName = language.LanguageName ?? "Technology";
            var allowedSubtopics = TechSubtopicCatalog.GetSubtopics(languageName);
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
                var prompt = BuildPrompt(languageName, level, currentBatchSize, questions.Count + 1, previousQuestions, focusAreas);

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
                        temperature = 0.7,
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



        private static string BuildPrompt(string languageName, string level, int count, int startNumber, HashSet<string> previousQuestions, List<string> focusAreas)
        {
            var exclusions = previousQuestions.Any()
                ? "Do not repeat or closely paraphrase these previous questions: " + string.Join(" | ", previousQuestions.Take(40)) + ". "
                : string.Empty;
            var difficultyInstruction = GetDifficultyInstruction(level);
            var focusText = string.Join(", ", focusAreas);

            return $"Generate exactly {count} multiple-choice quiz questions for the selected technology topic: {languageName}. " +
                   $"The questions must be about {languageName} only, not about a general course category. " +
                   $"Use only these selected subtopics/focus areas as the question source: {focusText}. " +
                   exclusions +
                   $"Mix the question types across: what the subtopic is used for, its function or purpose, advantages, disadvantages, usefulness, limitations, syntax or commands where relevant, debugging, security, practical scenarios, best practices, and real-world problem solving. " +
                   $"Difficulty level: {level}. {difficultyInstruction} " +
                   $"Make the options challenging and educational: every question must have exactly one correct answer. " +
                   $"At least two wrong options must look close to the real answer and should be realistic enough to confuse a learner. " +
                   $"The two strongest distractors should use common mistakes, near-correct syntax, incomplete best practices, reversed concepts, partial truths, or plausible but unsafe approaches. " +
                   $"Do not make wrong options silly, unrelated, impossible, or obviously false. " +
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
                new { Question = "In {0}, what is {2} mainly used for?", A = "Solving the specific project problem it was designed to handle", B = "Replacing every other feature in the application", C = "Improving the workflow only when it matches the requirement", D = "Avoiding validation, testing, and maintenance work", Correct = "Solving the specific project problem it was designed to handle" },
                new { Question = "What is the main function of {2} in {0}?", A = "To provide a focused capability that supports the application behavior", B = "To make all parts of the system run without configuration", C = "To support the application behavior when used in the right context", D = "To remove the need for readable code and documentation", Correct = "To provide a focused capability that supports the application behavior" },
                new { Question = "Which option is a real advantage of using {2} in {0}?", A = "It can make the solution clearer, safer, or easier to maintain", B = "It guarantees the project will have no bugs", C = "It can improve maintainability when applied correctly", D = "It makes performance perfect in every situation", Correct = "It can make the solution clearer, safer, or easier to maintain" },
                new { Question = "Which option is a possible disadvantage of using {2} in {0} incorrectly?", A = "It can add complexity or create bugs when used without understanding", B = "It always makes the application impossible to deploy", C = "It can become harder to maintain if applied to the wrong problem", D = "It permanently disables testing and debugging", Correct = "It can add complexity or create bugs when used without understanding" },
                new { Question = "When is {2} most useful in {0}?", A = "When the requirement matches the problem the concept is meant to solve", B = "When it is added to every file whether needed or not", C = "When it improves the design for the current requirement", D = "When the developer wants to avoid understanding the error", Correct = "When the requirement matches the problem the concept is meant to solve" },
                new { Question = "When debugging {2} in {0}, what should you check first?", A = "Reproduce the issue and inspect the relevant error or behavior", B = "Change unrelated settings until the error disappears", C = "Check the exact input, output, and error around the feature", D = "Delete the feature before reading the error message", Correct = "Reproduce the issue and inspect the relevant error or behavior" }
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
                "beginner" => "Ask foundation questions about meaning, use, function, advantages, disadvantages, simple examples, and common beginner mistakes.",
                "intermediate" => "Ask applied questions about practical use cases, normal project patterns, debugging, data flow, correct implementation choices, functions, advantages, and disadvantages.",
                "professional" => "Ask workplace-level questions about architecture decisions, security, maintainability, functions, advantages, disadvantages, performance tradeoffs, integration, testing, and production-style debugging.",
                "advanced" => "Ask advanced questions about optimization, internals, complex scenarios, scalability, edge cases, limitations, tradeoffs, and expert-level reasoning.",
                "expert" => "Ask expert questions about deep internals, advantages, disadvantages, architecture tradeoffs, failure modes, performance, and scenario-based problem solving.",
                _ => "Match the requested skill level and keep the questions practical."
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
