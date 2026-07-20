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

        public UserService(
            QuizContext context,
            ILogger<UserService> logger,
            IPasswordHasher<User> passwordHasher,
            IConfiguration config,
            IMemoryCache cache)
        {
            _context = context;
            _logger = logger;
            _passwordHasher = passwordHasher;
            _config = config;
            _cache = cache;
        }

        public async Task<CreateUserRequestModel> CreateUserAsync(CreateUserRequestModel request, CancellationToken cancellation)
        {
            ValidateRegistrationRequest(request);

            var email = NormalizeEmail(request.Email);
            var exists = await _context.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email.ToLower(), cancellation);

            if (exists)
            {
                _logger.LogWarning("Registration failed: Email {Email} already exists.", request.Email);
                throw new Exception("This email is already registered.");
            }

            var user = new User
            {
                Id = Guid.NewGuid(),
                UserName = request.UserName?.Trim(),
                Email = email,
                CreatedDate = DateTime.UtcNow
            };

            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password!);

            await _context.Users.AddAsync(user, cancellation);
            await _context.SaveChangesAsync(cancellation);

            return request;
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
    }
}
