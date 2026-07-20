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



        private static string BuildPrompt(string languageName, string level, int count, int startNumber, HashSet<string> previousQuestions, List<string> focusAreas, string? subtopicTrack)
        {
            var exclusions = previousQuestions.Any()
                ? "Do not repeat or closely paraphrase these previous questions: " + string.Join(" | ", previousQuestions.Take(40)) + ". "
                : string.Empty;
            var difficultyInstruction = GetDifficultyInstruction(level);
            var focusText = string.Join(", ", focusAreas);
            var trackInstruction = GetTrackInstruction(subtopicTrack);

            return $"Generate exactly {count} multiple-choice quiz questions for the selected technology topic: {languageName}. " +
                   $"The questions must be about {languageName} only, not about a general course category. " +
                   $"Use only these selected subtopics/focus areas as the question source: {focusText}. " +
                   exclusions +
                   $"{trackInstruction} " +
                   $"Make 90 percent of the questions practical, scenario-based, and code-focused. Only 10 percent may be pure theory. Put the learner inside real life software tasks such as building a login flow, fixing a bug, completing a controller/action, choosing a SQL query, securing an endpoint, debugging an API request, refactoring a method, reading a stack trace, or completing a small code snippet. " +
                   $"Ask what code should replace a TODO, which method call completes the feature, which snippet produces the required behavior, which implementation is safest, which bug causes the output, and which tradeoff matters in a real project. " +
                   $"Avoid shallow questions like 'What is X?' unless they are part of the 10 percent theory portion. Most questions must start from a realistic task, bug report, code review, production issue, database/API request, UI behavior, security concern, or deployment scenario. " +
                   $"Every practical question should mention a concrete situation and force the learner to choose an action, code line, query, command, method, fix, or design decision. " +
                   $"The small 10 percent theory portion may ask about purpose, advantages, disadvantages, usefulness, limitations, syntax, commands, or best practices, but it must still connect to the selected subtopic. " +
                   $"When the topic supports code, at least 90 percent of the questions must include a short code snippet, command, query, method signature, configuration sample, terminal output, stack trace, or pseudo-code scenario inside the question text. " +
                   $"Difficulty level: {level}. {difficultyInstruction} " +
                   $"Keep the difficulty strict: Beginner must not use professional architecture questions; Intermediate must be more applied than Beginner; Professional must include workplace code decisions and production tradeoffs; Advanced must be the hardest with complex edge cases and deep reasoning. " +
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
                new { Question = "A developer is building a {0} feature that uses {2}. The code has a TODO where the main logic should be placed. What should the developer focus on first?", A = "Implement the exact behavior required by the feature and validate the input/output", B = "Add the most advanced-looking syntax even if it is not needed", C = "Copy a similar block without checking if it matches the requirement", D = "Skip validation so the code runs faster during testing", Correct = "Implement the exact behavior required by the feature and validate the input/output" },
                new { Question = "In a real {0} project, a bug appears after adding {2}. What is the best first debugging step?", A = "Reproduce the bug, inspect the failing input, and trace the code path around {2}", B = "Rewrite unrelated files until the error disappears", C = "Ignore the error if the page still loads sometimes", D = "Delete the feature before checking logs or output", Correct = "Reproduce the bug, inspect the failing input, and trace the code path around {2}" },
                new { Question = "A teammate asks which implementation of {2} should be used in a {0} production task. Which answer is best?", A = "Choose the implementation that satisfies the requirement, is testable, and is easiest to maintain", B = "Choose the shortest code even if it hides errors", C = "Choose the version with the most complex syntax", D = "Choose any version because all implementations behave the same", Correct = "Choose the implementation that satisfies the requirement, is testable, and is easiest to maintain" },
                new { Question = "A {0} code review finds this issue around {2}: the feature works for one case but fails for invalid input. What correction is most practical?", A = "Add input validation and handle the failure path clearly", B = "Remove all error messages from the application", C = "Assume users will always enter correct data", D = "Move the code to another file without changing the logic", Correct = "Add input validation and handle the failure path clearly" },
                new { Question = "A user reports that a {0} feature using {2} is slow in a real project. Which option is the most useful investigation?", A = "Measure the slow step, inspect queries or loops, and optimize only the proven bottleneck", B = "Optimize every file before measuring anything", C = "Disable the feature permanently", D = "Change the UI color because performance is mostly visual", Correct = "Measure the slow step, inspect queries or loops, and optimize only the proven bottleneck" },
                new { Question = "A junior developer writes a {0} solution with {2} but leaves security checks out. What is the safest correction?", A = "Add proper validation, authorization, and safe handling for the feature's data", B = "Trust all client-side input because the UI looks correct", C = "Store sensitive data in plain text for easier debugging", D = "Let every logged-in user perform every action", Correct = "Add proper validation, authorization, and safe handling for the feature's data" },
                new { Question = "Complete the practical idea for a {0} task using {2}: before saving data, the method should ____.", A = "validate the request, handle errors, and save only correct data", B = "save first and check errors only after deployment", C = "ignore missing values to reduce code size", D = "return success before the operation finishes", Correct = "validate the request, handle errors, and save only correct data" },
                new { Question = "A {0} project has two possible solutions for {2}. One is quick but hard to test; the other is clear and testable. Which should be selected for a serious project?", A = "The clear and testable solution, because maintenance matters after the first release", B = "The hard-to-test solution because tests are optional", C = "The solution with fewer lines even if it breaks edge cases", D = "The solution that hides errors from the user and logs", Correct = "The clear and testable solution, because maintenance matters after the first release" },
                new { Question = "When using {2} in {0}, which option best explains its practical purpose?", A = "It helps solve a specific development problem when applied to the right requirement", B = "It replaces every other concept in the technology", C = "It guarantees no bug can ever happen", D = "It removes the need for testing, debugging, and documentation", Correct = "It helps solve a specific development problem when applied to the right requirement" },
                new { Question = "Which disadvantage can happen if {2} is used wrongly in a {0} project?", A = "The code can become harder to maintain or may introduce hidden bugs", B = "The whole technology becomes unusable forever", C = "The computer cannot compile any other project again", D = "All secure coding practices become unnecessary", Correct = "The code can become harder to maintain or may introduce hidden bugs" }
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
                "beginner" => "Ask easy but meaningful foundation questions using simple real-life examples. Include short one-step snippets, basic method/function behavior, simple syntax completion, advantages, disadvantages, and common beginner mistakes. Avoid architecture, deep optimization, or production deployment questions.",
                "intermediate" => "Ask moderately complex applied questions. Use small real project scenarios, debugging, data flow, method selection, implementation choices, two-step code reasoning, advantages, and disadvantages. Make the questions clearly harder than Beginner.",
                "professional" => "Ask very complex workplace-level questions. Include realistic code snippets, design decisions, security, maintainability, testing, integration, performance tradeoffs, production debugging, and choosing the best method or pattern for a real project.",
                "pro" => "Ask very complex workplace-level questions. Include realistic code snippets, design decisions, security, maintainability, testing, integration, performance tradeoffs, production debugging, and choosing the best method or pattern for a real project.",
                "advanced" => "Ask hard and very complex questions. Use advanced code behavior, optimization, internals, scalability, edge cases, limitations, tradeoffs, failure modes, architecture reasoning, and expert-level scenarios.",
                "expert" => "Ask hard and very complex questions. Use deep internals, advantages, disadvantages, architecture tradeoffs, failure modes, performance, security, and scenario-based problem solving.",
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
