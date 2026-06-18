using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quiz_Application.Interface.Service;
using Quiz_Application.Models.DTO.User;
using Quiz_Application.Interfaces.Repositories; 
using System.Security.Claims;

namespace Quiz_Application.Controllers
{
    [Authorize]
    public class UserController : Controller
    {
        private readonly IUserService _userService;
        private readonly ICourseService _courseService;
        private readonly IQuizService _quizService; 
        private readonly ILogger<UserController> _logger;

        public UserController(
            IUserService userService,
            ICourseService courseService,
            IQuizService quizService,
            ILogger<UserController> logger)
        {
            _userService = userService;
            _courseService = courseService;
            _quizService = quizService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> Dashboard(CancellationToken cancellation)
        {
            var userId = GetCurrentUserId();
            if (userId == Guid.Empty) return RedirectToAction("Login");

            try
            {
                var userProfile = await _userService.GetUserProfileByUserId(userId, cancellation);
                if (userProfile == null) return NotFound("User profile not found.");

                var userQuizzes = await _quizService.GetQuizzesByUserIdAsync(userId, cancellation);

                ViewBag.RecentQuizzes = userQuizzes.Take(5); 

                return View(userProfile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Q-MSTech Dashboard for user {UserId}", userId);
                return View("Error");
            }
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginRequestModel request, bool rememberMe, string? returnUrl, CancellationToken cancellation)
        {
            ViewData["ReturnUrl"] = returnUrl;

            if (!ModelState.IsValid)
            {
                return View(request);
            }

            try
            {
                var result = await _userService.LoginAsync(request, cancellation);

                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Login failed for user: {Email}", request.Email);
                    TempData["Error"] = result.Message;
                    return View(request);
                }

                var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, result.UserName ?? "User"),
            new Claim(ClaimTypes.Email, result.Email ?? ""),
            new Claim(ClaimTypes.NameIdentifier, result.UserId.ToString())
        };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    new AuthenticationProperties
                    {
                        IsPersistent = rememberMe,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
                    });

                HttpContext.Session.SetString("UserId", result.UserId.ToString());
                HttpContext.Session.SetString("Username", result.UserName ?? "");

                TempData["Message"] = "Authentication Successful. Neural link established.";

                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }

                return RedirectToAction("Dashboard");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical failure during login for {Email}", request.Email);
                TempData["Error"] = "Connection to Neural Hub interrupted. Please try again.";
                return View(request);
            }
        }


        [AllowAnonymous]
        [HttpGet]
        public IActionResult Register() => View();

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(CreateUserRequestModel request, CancellationToken cancellation)
        {
            try
            {
                await _userService.BeginRegistrationAsync(request, cancellation);
                TempData["Message"] = "We sent a confirmation code to your email.";
                return RedirectToAction(nameof(ConfirmEmail), new { email = request.Email });
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return View(request);
            }
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult ConfirmEmail(string email)
        {
            return View(new ConfirmEmailRequestModel { Email = email });
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequestModel request, CancellationToken cancellation)
        {
            if (!ModelState.IsValid)
            {
                return View(request);
            }

            try
            {
                await _userService.ConfirmRegistrationAsync(request, cancellation);
                TempData["Message"] = "Email confirmed. You can now log in.";
                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return View(request);
            }
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult ForgotPassword() => View(new ForgotPasswordRequestModel());

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ForgotPassword(ForgotPasswordRequestModel request, CancellationToken cancellation)
        {
            if (!ModelState.IsValid)
            {
                return View(request);
            }

            await _userService.BeginPasswordResetAsync(request, cancellation);
            TempData["Message"] = "If the email exists, a reset code has been sent.";
            return RedirectToAction(nameof(ResetPassword), new { email = request.Email });
        }

        [AllowAnonymous]
        [HttpGet]
        public IActionResult ResetPassword(string email)
        {
            return View(new ResetPasswordRequestModel { Email = email });
        }

        [AllowAnonymous]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPassword(ResetPasswordRequestModel request, CancellationToken cancellation)
        {
            if (!ModelState.IsValid)
            {
                return View(request);
            }

            try
            {
                await _userService.ResetPasswordAsync(request, cancellation);
                TempData["Message"] = "Password changed successfully. Please log in.";
                return RedirectToAction(nameof(Login));
            }
            catch (Exception ex)
            {
                TempData["Error"] = ex.Message;
                return View(request);
            }
        }

        [HttpGet]
        public async Task<IActionResult> Logout()
        {
            HttpContext.Session.Clear();
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            TempData["Message"] = "Session terminated. You have been logged out.";
            return RedirectToAction("Login");
        }

        private Guid GetCurrentUserId()
        {
            var userIdSession = HttpContext.Session.GetString("UserId");
            if (!string.IsNullOrEmpty(userIdSession) && Guid.TryParse(userIdSession, out Guid userId))
                return userId;

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out Guid claimId))
                return claimId;

            return Guid.Empty;
        }
    }
}
