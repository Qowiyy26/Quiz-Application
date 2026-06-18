using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quiz_Application.Interface.Service;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO;
using Quiz_Application.Models.DTO.Question;
using System.Security.Claims;

namespace Quiz_Application.Controllers
{
    [Authorize]
    public class QuizController : Controller
    {
        private readonly IQuizService _quizService;
        private readonly IQuestionService _questionService;
        private readonly ISuggestionService _suggestionService;
        private readonly ILanguageService _languageService;
        private readonly ILogger<QuizController> _logger;

        public QuizController(
            IQuizService quizService,
            IQuestionService questionService,
            ISuggestionService suggestionService,
            ILanguageService languageService,
            ILogger<QuizController> logger)
        {
            _quizService = quizService;
            _questionService = questionService;
            _suggestionService = suggestionService;
            _languageService = languageService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Start(Guid id, CancellationToken cancellation)
        {
            if (id == Guid.Empty) return RedirectToAction("Dashboard", "User");

            var quiz = await _quizService.GetQuizByIdAsync(id, cancellation);

            if (quiz == null)
            {
                _logger.LogWarning("Quiz with ID {QuizId} not found.", id);
                TempData["Error"] = "The requested neural stream does not exist.";
                return RedirectToAction("Dashboard", "User");
            }

            return View("TakeQuiz", quiz); 
        }

        [HttpGet]
        public async Task<IActionResult> Index(Guid languageId, string? languageName, CancellationToken cancellation)
        {
            if (languageId == Guid.Empty) return RedirectToAction("Index", "Course");

            var language = await _languageService.GetLanguageByIdAsync(languageId, cancellation);
            if (language == null)
            {
                TempData["Error"] = "Selected module was not found. Please choose a course module again.";
                return RedirectToAction("Index", "Course");
            }

            var selectedLanguageName = string.IsNullOrWhiteSpace(languageName) ? language.LanguageName : languageName;
            var model = new QuizSetupViewModel
            {
                LanguageId = languageId,
                LanguageName = selectedLanguageName ?? "Unknown Topic",
                Subtopics = TechSubtopicCatalog.GetSubtopics(selectedLanguageName)
            };

            return View(model);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Generate(Guid languageId, string level, int questionCount, List<string>? selectedSubtopics, CancellationToken cancellation)
        {
            if (languageId == Guid.Empty || string.IsNullOrWhiteSpace(level))
            {
                TempData["Error"] = "Please select a language and level before starting.";
                return RedirectToAction("Index", "Course");
            }

            var userId = GetCurrentUserId();

            try
            {
                var quiz = await _quizService.GenerateQuizAsync(userId, languageId, level, questionCount, selectedSubtopics, cancellation);
                return View("TakeQuiz", quiz);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Generate quiz failed — language {LanguageId} not found", languageId);
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(Index), new { languageId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini API failed to generate quiz for language {LanguageId}, level {Level}", languageId, level);
                TempData["Error"] = "AI engine could not generate questions. Please try again.";
                return RedirectToAction(nameof(Index), new { languageId });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(Guid quizId, Dictionary<Guid, string> answers, CancellationToken cancellation)
        {
            if (quizId == Guid.Empty)
            {
                TempData["Error"] = "Invalid quiz session.";
                return RedirectToAction("Index", "Course");
            }

            try
            {
                var result = await _quizService.SubmitQuizAsync(quizId, answers, cancellation);

                TempData["Score"] = result.Score;
                TempData["TotalQuestions"] = result.TotalQuestions;

                return RedirectToAction(nameof(Result), new { quizId, resultId = result.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit quiz {QuizId}", quizId);
                TempData["Error"] = "Submission failed. Please try again.";
                return RedirectToAction("Index", "Course");
            }
        }

        [HttpGet]
        public async Task<IActionResult> Result(Guid quizId, CancellationToken cancellation)
        {
            if (quizId == Guid.Empty) return RedirectToAction("Index", "Course");

            var resultText = await _quizService.GetQuizResultAsync(quizId, cancellation);
            ViewBag.ResultText = resultText;
            ViewBag.QuizId = quizId;
            ViewBag.ResultId = Request.Query["resultId"].ToString();

            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Review(Guid quizId, CancellationToken cancellation)
        {
            if (quizId == Guid.Empty)
            {
                TempData["Error"] = "Invalid quiz review request.";
                return RedirectToAction(nameof(History));
            }

            var review = await _quizService.GetQuizReviewAsync(quizId, cancellation);
            if (review == null)
            {
                TempData["Error"] = "Could not find the selected quiz review.";
                return RedirectToAction(nameof(History));
            }

            return View(review);
        }

        [HttpGet]
        public async Task<IActionResult> Suggestions(Guid quizResultId)
        {
            if (quizResultId == Guid.Empty)
            {
                TempData["Error"] = "Invalid result ID.";
                return RedirectToAction(nameof(History));
            }

            try
            {
                var cached = _suggestionService.GetSuggestionsFromCache(quizResultId);
                if (cached != null && cached.Any())
                {
                    return View(cached);
                }

                var suggestions = await _suggestionService.GetSuggestionsAsync(quizResultId);
                return View(suggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get suggestions for result {QuizResultId}", quizResultId);
                TempData["Error"] = "Could not load suggestions. Please try again.";
                return RedirectToAction(nameof(History));
            }
        }

        [HttpGet]
        public async Task<IActionResult> History(CancellationToken cancellation)
        {
            var userId = GetCurrentUserId();

            try
            {
                var quizzes = await _quizService.GetQuizzesByUserIdAsync(userId, cancellation);
                return View(quizzes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quiz history for user {UserId}", userId);
                TempData["Error"] = "Could not load quiz history.";
                return View(Enumerable.Empty<QuizDTO>());
            }
        }

        [HttpGet]
        public async Task<IActionResult> Details(Guid quizId, CancellationToken cancellation)
        {
            if (quizId == Guid.Empty) return NotFound();

            var quiz = await _quizService.GetQuizByIdAsync(quizId, cancellation);
            if (quiz == null) return NotFound();

            return View("TakeQuiz", quiz);
        }

        [HttpGet]
        public async Task<IActionResult> Question(Guid questionId, CancellationToken cancellation)
        {
            if (questionId == Guid.Empty) return NotFound();

            var question = await _questionService.GetQuestionByIdAsync(questionId, cancellation);
            if (question == null) return NotFound();

            return Json(question);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteQuestion(Guid questionId, CancellationToken cancellation)
        {
            if (questionId == Guid.Empty) return NotFound();

            var deleted = await _questionService.DeleteQuestionAsync(questionId, cancellation);

            if (!deleted)
            {
                _logger.LogWarning("DeleteQuestion: Question {QuestionId} not found", questionId);
                return NotFound();
            }

            _logger.LogInformation("Question {QuestionId} deleted successfully", questionId);
            return RedirectToAction(nameof(History));
        }

        [HttpGet]
        public async Task<IActionResult> AllQuizzes(CancellationToken cancellation)
        {
            var quizzes = await _quizService.GetAllQuizzesAsync(cancellation);
            return Content(quizzes ?? "[]", "application/json");
        }

        private Guid GetCurrentUserId()
        {
            var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
    }
}
