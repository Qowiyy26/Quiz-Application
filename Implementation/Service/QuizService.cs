using Quiz_Application.Interface.Repository;
using Quiz_Application.Interface.Service;
using Quiz_Application.Interfaces.Repositories;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO;
using Quiz_Application.Models.DTO.Question;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quiz_Application.DBCONTEXT;

namespace Quiz_Application.Implementation.Service
{
    public class QuizService : IQuizService
    {
        private readonly IQuizRepository _quizRepository;
        private readonly IQuestionService _questionService;
        private readonly IResultService _resultService;
        private readonly ILanguageRepository _languageRepository;
        private readonly ISuggestionService _suggestionService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<QuizService> _logger;
        private readonly QuizContext _dbContext;

        public QuizService(
            IQuizRepository quizRepository,
            IQuestionService questionService,
            IResultService resultService,
            ILanguageRepository languageRepository,
            ISuggestionService suggestionService,
            IUnitOfWork unitOfWork,
            ILogger<QuizService> logger,
            QuizContext dbContext)
        {
            _quizRepository = quizRepository;
            _questionService = questionService;
            _resultService = resultService;
            _languageRepository = languageRepository;
            _suggestionService = suggestionService;
            _unitOfWork = unitOfWork;
            _logger = logger;
            _dbContext = dbContext;
        }

        public async Task<QuizDTO> GenerateQuizAsync(Guid userId, Guid languageId, string level, int questionCount, int durationMinutes, IEnumerable<string>? selectedSubtopics, CancellationToken cancellation)
        {
            var language = await _languageRepository.GetLanguageByIdAsync(languageId);
            if (language == null)
            {
                _logger.LogWarning("Quiz generation failed: Language {LanguageId} not found.", languageId);
                throw new InvalidOperationException("Simulation Error: Technical Stack not found.");
            }

            var quiz = new Quiz
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                LanguageId = languageId,
                Level = level,
                CreatedDate = DateTime.UtcNow
            };

            await _quizRepository.CreateQuiz(quiz);

            var requestedCount = questionCount > 0 ? Math.Clamp(questionCount, 20, 150) : 20;
            var requestedDuration = Math.Clamp(durationMinutes, 5, 60);
            var aiQuestions = await _questionService.GenerateQuestionsFromApiAsync(languageId, level, requestedCount, userId, selectedSubtopics, cancellation);

            if (aiQuestions == null || !aiQuestions.Any())
            {
                _logger.LogError("AI Engine returned 0 questions for {Language} - {Level}", language.LanguageName, level);
                throw new Exception("AI Engine failed to generate questions for this stack.");
            }

            foreach (var question in aiQuestions)
            {
                question.LanguageId = languageId;
                question.Difficulty = level;
            }

            await _questionService.SaveQuestionsAsync(quiz.Id, aiQuestions, cancellation);
            await _unitOfWork.SaveChangesAsync(cancellation);

            return new QuizDTO
            {
                Id = quiz.Id,
                Level = quiz.Level,
                LanguageName = language.LanguageName ?? "Unknown Tech",
                DurationMinutes = requestedDuration,
                CreatedDate = quiz.CreatedDate,
                Questions = aiQuestions.ToList()
            };
        }

        public async Task<ResultDTO> SubmitQuizAsync(Guid quizId, Dictionary<Guid, string> userAnswers, CancellationToken cancellation)
        {
            var quiz = await _quizRepository.GetQuizById(quizId);
            if (quiz == null)
            {
                throw new InvalidOperationException("Quiz session was not found.");
            }

            userAnswers ??= new Dictionary<Guid, string>();
            int score = 0;

            foreach (var question in quiz.Questions)
            {
                if (userAnswers.TryGetValue(question.Id, out string? submittedAnswer))
                {
                    if (string.Equals(submittedAnswer?.Trim(), question.CorrectOption?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        score++;
                    }
                }
            }

            var responses = quiz.Questions.Select(question => new UserResponse
            {
                Id = Guid.NewGuid(),
                QuestionId = question.Id,
                SelectedOption = userAnswers.TryGetValue(question.Id, out var selectedAnswer) ? selectedAnswer : string.Empty,
                Timestamp = DateTime.UtcNow
            }).ToList();

            await _dbContext.UserResponses.AddRangeAsync(responses, cancellation);

            var result = new Result
            {
                Id = Guid.NewGuid(),
                QuizId = quizId,
                UserId = quiz.UserId,
                Score = score,
                TotalQuestions = quiz.Questions.Count,
                CorrectAnswers = score,
                CreatedDate = DateTime.UtcNow
            };

            quiz.Score = score;
            quiz.TotalQuestions = quiz.Questions.Count;

            await _quizRepository.SaveResult(result);
            await _unitOfWork.SaveChangesAsync(cancellation);

            return new ResultDTO { Id = result.Id, QuizId = result.QuizId, Score = score, TotalQuestions = quiz.Questions.Count };
        }


        public async Task<string?> GetQuizResultAsync(Guid quizId, CancellationToken cancellationToken)
        {
            var quiz = await _quizRepository.GetQuizById(quizId);
            if (quiz == null)
            {
                return "Quiz not found.";
            }

            var result = await _quizRepository.GetResultByQuizId(quizId);

            if (result == null)
            {
                return "No result recorded for this session.";
            }

            double percentage = result.TotalQuestions > 0
                ? ((double)result.Score / result.TotalQuestions) * 100
                : 0;

            return $"Score: {result.Score}/{result.TotalQuestions} ({percentage:F1}%)";
        }

        public async Task<QuizDTO?> GetQuizByIdAsync(Guid quizId, CancellationToken cancellation)
        {
            var quiz = await _quizRepository.GetQuizById(quizId);
            if (quiz == null)
            {
                return null;
            }

            var questions = await _questionService.GetQuestionsByQuizIdAsync(quizId, cancellation);

            return new QuizDTO
            {
                Id = quiz.Id,
                Level = quiz.Level,
                LanguageName = quiz.Language?.LanguageName ?? "Unknown",
                DurationMinutes = Math.Max(quiz.TotalQuestions ?? questions.Count(), 1),
                CreatedDate = quiz.CreatedDate,
                Questions = questions.ToList()
            };
        }

        public async Task<QuizDTO?> GetQuizReviewAsync(Guid quizId, CancellationToken cancellation)
        {
            var quiz = await _dbContext.Quizzes
                .Include(q => q.Language)
                .Include(q => q.Result)
                .Include(q => q.Questions)
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == quizId, cancellation);

            if (quiz == null) return null;

            var questionIds = quiz.Questions.Select(q => q.Id).ToList();
            var responses = await _dbContext.UserResponses
                .Where(r => questionIds.Contains(r.QuestionId))
                .AsNoTracking()
                .ToListAsync(cancellation);

            var latestResponses = responses
                .GroupBy(r => r.QuestionId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Timestamp).First().SelectedOption ?? string.Empty);

            var questions = quiz.Questions.Select(q =>
            {
                latestResponses.TryGetValue(q.Id, out var userSelection);

                return new QuestionDTO
                {
                    Id = q.Id,
                    QuestionText = q.QuestionText,
                    OptionA = q.OptionA,
                    OptionB = q.OptionB,
                    OptionC = q.OptionC,
                    OptionD = q.OptionD,
                    CorrectAnswer = q.CorrectOption,
                    CorrectOption = q.CorrectOption,
                    UserSelection = userSelection ?? string.Empty,
                    QuizId = q.QuizId,
                    LanguageId = q.LanguageId,
                    Difficulty = q.Difficulty
                };
            }).ToList();

            return new QuizDTO
            {
                Id = quiz.Id,
                Level = quiz.Level,
                LanguageName = quiz.Language?.LanguageName ?? "Unknown",
                CreatedDate = quiz.CreatedDate,
                DurationMinutes = Math.Max(quiz.Result?.TotalQuestions ?? quiz.TotalQuestions ?? questions.Count, 1),
                Score = quiz.Result?.Score ?? quiz.Score ?? 0,
                TotalQuestions = quiz.Result?.TotalQuestions ?? quiz.TotalQuestions ?? questions.Count,
                Questions = questions
            };
        }

        public async Task<IEnumerable<QuizDTO>> GetQuizzesByUserIdAsync(Guid userId, CancellationToken cancellation)
        {
            var quizzes = await _quizRepository.GetQuizzesByUserId(userId);

            return quizzes
                .Select(MapToDto)
                .OrderByDescending(q => q.CreatedDate)
                .ToList();
        }

        public async Task<string?> GetAllQuizzesAsync(CancellationToken cancellation)
        {
            var quizzes = await _quizRepository.GetAllQuizzes();

            if (quizzes == null || !quizzes.Any())
            {
                return null;
            }

            var dtos = quizzes.Select(MapToDto);

            return JsonSerializer.Serialize(dtos, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        private static QuizDTO MapToDto(Quiz q) => new QuizDTO
        {
            Id = q.Id,
            Level = q.Level,
            LanguageName = q.Language?.LanguageName ?? "Unknown",
            Score = q.Result?.Score ?? q.Score ?? 0,
            TotalQuestions = q.Result?.TotalQuestions ?? q.TotalQuestions ?? 0,
            CreatedDate = q.CreatedDate
        };

    }
}
