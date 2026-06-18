using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using Quiz_Application.DBCONTEXT;
using Quiz_Application.Interface.Service;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO.User;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Quiz_Application.Implementation.Service
{
    public class UserService : IUserService
    {
        private readonly QuizContext _context;
        private readonly ILogger<UserService> _logger;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly IEmailService _emailService;

        public UserService(
            QuizContext context,
            ILogger<UserService> logger,
            IPasswordHasher<User> passwordHasher,
            IConfiguration config,
            IMemoryCache cache,
            IEmailService emailService)
        {
            _context = context;
            _logger = logger;
            _passwordHasher = passwordHasher;
            _config = config;
            _cache = cache;
            _emailService = emailService;
        }

        private const int CodeMinutes = 15;

        public async Task<CreateUserRequestModel> CreateUserAsync(CreateUserRequestModel request, CancellationToken cancellation)
        {
            var email = request.Email ?? string.Empty;
            var exists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);

            if (exists)
            {
                _logger.LogWarning("Registration failed: Email {Email} already exists.", request.Email);
                throw new Exception("This email is already registered.");
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = request.UserName,
                Email = email,
                CreatedDate = DateTime.UtcNow
            };

            if (!string.IsNullOrEmpty(request.Password))
            {
                user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
            }

            await _context.Users.AddAsync(user, cancellation);
            await _context.SaveChangesAsync(cancellation);

            return request;
        }

        public async Task BeginRegistrationAsync(CreateUserRequestModel request, CancellationToken cancellation)
        {
            ValidateRegistrationRequest(request);

            var email = NormalizeEmail(request.Email);
            var exists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);

            if (exists)
            {
                throw new Exception("This email is already registered.");
            }

            var pendingUser = new User
            {
                Id = Guid.NewGuid(),
                UserName = request.UserName?.Trim(),
                Email = email,
                CreatedDate = DateTime.UtcNow
            };
            pendingUser.PasswordHash = _passwordHasher.HashPassword(pendingUser, request.Password!);

            var code = GenerateSecurityCode();
            var cacheEntry = new PendingRegistration(pendingUser, HashCode(code));
            _cache.Set(RegistrationCacheKey(email), cacheEntry, TimeSpan.FromMinutes(CodeMinutes));

            await _emailService.SendAsync(
                email,
                "Confirm your Q-MSTech account",
                BuildCodeEmail("Confirm your account", code, "Use this code to complete your Q-MSTech account creation."),
                cancellation);
        }

        public async Task ConfirmRegistrationAsync(ConfirmEmailRequestModel request, CancellationToken cancellation)
        {
            var email = NormalizeEmail(request.Email);
            var code = request.Code?.Trim() ?? string.Empty;

            if (!_cache.TryGetValue(RegistrationCacheKey(email), out PendingRegistration? pending) ||
                pending == null ||
                !FixedTimeEquals(pending.CodeHash, HashCode(code)))
            {
                throw new Exception("Invalid or expired confirmation code.");
            }

            var exists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);
            if (exists)
            {
                _cache.Remove(RegistrationCacheKey(email));
                throw new Exception("This email is already registered.");
            }

            await _context.Users.AddAsync(pending.User, cancellation);
            await _context.SaveChangesAsync(cancellation);
            _cache.Remove(RegistrationCacheKey(email));
        }

        public async Task<LoginResponseModel> LoginAsync(LoginRequestModel request, CancellationToken cancellation)
        {
            var user = await _context.Users
                .Include(u => u.Course)
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == (request.Email ?? string.Empty).ToLower(), cancellation);

            if (user == null || _passwordHasher.VerifyHashedPassword(user, user.PasswordHash!, request.Password!) == PasswordVerificationResult.Failed)
            {
                return new LoginResponseModel { IsSuccess = false, Message = "Invalid credentials." };
            }

            var token = GenerateJwtToken(user);

            return new LoginResponseModel
            {
                IsSuccess = true,
                Token = token, 
                UserId = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                User = MapToDto(user)
            };
        }

        public async Task BeginPasswordResetAsync(ForgotPasswordRequestModel request, CancellationToken cancellation)
        {
            var email = NormalizeEmail(request.Email);
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);

            if (user == null)
            {
                _logger.LogInformation("Password reset requested for unknown email {Email}.", email);
                return;
            }

            var code = GenerateSecurityCode();
            _cache.Set(PasswordResetCacheKey(email), HashCode(code), TimeSpan.FromMinutes(CodeMinutes));

            await _emailService.SendAsync(
                email,
                "Reset your Q-MSTech password",
                BuildCodeEmail("Reset your password", code, "Use this code to confirm that you requested a password change."),
                cancellation);
        }

        public async Task ResetPasswordAsync(ResetPasswordRequestModel request, CancellationToken cancellation)
        {
            var email = NormalizeEmail(request.Email);
            var code = request.Code?.Trim() ?? string.Empty;

            if (!_cache.TryGetValue(PasswordResetCacheKey(email), out string? expectedHash) ||
                string.IsNullOrWhiteSpace(expectedHash) ||
                !FixedTimeEquals(expectedHash, HashCode(code)))
            {
                throw new Exception("Invalid or expired reset code.");
            }

            ValidatePassword(request.NewPassword);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation)
                ?? throw new Exception("Invalid or expired reset code.");

            user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword!);
            await _context.SaveChangesAsync(cancellation);
            _cache.Remove(PasswordResetCacheKey(email));
        }

        public async Task<UserDTO?> GetUserProfileByUserId(Guid userId, CancellationToken cancellation)
        {
            string cacheKey = $"UserProfile_{userId}";
            if (_cache.TryGetValue(cacheKey, out UserDTO? cachedProfile) && cachedProfile != null) return cachedProfile;

            var user = await _context.Users
                .Include(u => u.Course)
                .FirstOrDefaultAsync(u => u.Id == userId, cancellation);

            if (user == null) return null;

            var profile = MapToDto(user);
            _cache.Set(cacheKey, profile, TimeSpan.FromMinutes(10));

            return profile;
        }

        private string GenerateJwtToken(User user)
        {
            var jwtSettings = _config.GetSection("JwtSettings");
            var secretKey = jwtSettings["SecretKey"] ?? "development-secret-key-change-this-value";
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName ?? "User"),
                new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: jwtSettings["Issuer"] ?? "QuizApplication",
                audience: jwtSettings["Audience"] ?? "QuizApplication",
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(double.Parse(jwtSettings["ExpiryMinutes"] ?? "30")),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private UserDTO MapToDto(User user) => new UserDTO
        {
            Id = user.Id,
            UserName = user.UserName ?? "User",
            Email = user.Email ?? string.Empty,
            CreatedDate = user.CreatedDate,
            PreferredCourse = user.Course?.CourseName ?? "No Course Selected"
        };

        public async Task<int> GetAllUsersCount(CancellationToken cancellation) =>
            await _context.Users.CountAsync(cancellation);

        public async Task<UserDTO?> GetUserByEmail(string email, CancellationToken cancellation)
        {
            var user = await _context.Users.Include(u => u.Course)
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);
            return user == null ? null : MapToDto(user);
        }

        private static void ValidateRegistrationRequest(CreateUserRequestModel request)
        {
            if (string.IsNullOrWhiteSpace(request.UserName))
            {
                throw new Exception("Username is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Email))
            {
                throw new Exception("Email is required.");
            }

            ValidatePassword(request.Password);

            if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
            {
                throw new Exception("Passwords do not match.");
            }
        }

        private static void ValidatePassword(string? password)
        {
            if (string.IsNullOrWhiteSpace(password) ||
                password.Length < 8 ||
                !password.Any(char.IsUpper) ||
                !password.Any(char.IsLower) ||
                !password.Any(char.IsDigit))
            {
                throw new Exception("Password must be at least 8 characters and include uppercase, lowercase, and a number.");
            }
        }

        private static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

        private static string GenerateSecurityCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        private static string HashCode(string code)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            return Convert.ToHexString(bytes);
        }

        private static bool FixedTimeEquals(string expected, string actual)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            return expectedBytes.Length == actualBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }

        private static string RegistrationCacheKey(string email) => $"RegistrationCode_{email}";

        private static string PasswordResetCacheKey(string email) => $"PasswordResetCode_{email}";

        private static string BuildCodeEmail(string title, string code, string message)
        {
            return $"""
                <div style="font-family:Inter,Arial,sans-serif;line-height:1.6;color:#0f172a">
                    <h2>{title}</h2>
                    <p>{message}</p>
                    <p style="font-size:28px;font-weight:800;letter-spacing:6px;background:#eef2ff;padding:14px 18px;border-radius:8px;display:inline-block">{code}</p>
                    <p>This code expires in {CodeMinutes} minutes. If you did not request this, ignore this email.</p>
                </div>
                """;
        }

        private sealed record PendingRegistration(User User, string CodeHash);
    }
}
