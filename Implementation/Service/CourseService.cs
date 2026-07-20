using Microsoft.Extensions.Caching.Memory;
using Quiz_Application.Interface.Repository;
using Quiz_Application.Interface.Service;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO;
using Quiz_Application.Models.Enum;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Quiz_Application.Implementation.Service
{
    public class CourseService : ICourseService
    {
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly ICourseRepository _courseRepository;
        private readonly ILanguageRepository _languageRepository;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CourseService> _logger;

        public CourseService(
            ICourseRepository courseRepository,
            ILanguageRepository languageRepository,
            HttpClient httpClient,
            IConfiguration config,
            IMemoryCache cache,
            ILogger<CourseService> logger)
        {
            _config = config;
            _clientId = _config["Lightcast:clientId"] ?? string.Empty;
            _clientSecret = _config["Lightcast:clientSecret"] ?? string.Empty;
            _courseRepository = courseRepository;
            _languageRepository = languageRepository;
            _httpClient = httpClient;
            _cache = cache;
            _logger = logger;
        }

        public async Task<List<string>> GetExternalCategoriesAsync()
        {
            const string cacheKey = "Lightcast_Programming_Skills";
            if (_cache.TryGetValue(cacheKey, out List<string>? cachedSkills) && cachedSkills != null) return cachedSkills;

            var skills = GetDefaultTechCourses().OrderBy(x => x).ToList();
            _cache.Set(cacheKey, skills, TimeSpan.FromHours(12));
            return await Task.FromResult(skills);
        }

        private static List<string> GetDefaultTechCourses()
        {
            return new List<string> {
                "Web Application", "Web Application Development", "Programming Languages", "Software Development",
                "Networking", "Artificial Intelligence", "Database Systems", "Cloud Computing",
                "Python Development", "Java Enterprise", "C# .NET", "C++ Systems", "C Programming",
                "JavaScript/TypeScript", "Go (Golang)", "Rust Systems", "Swift iOS", "Kotlin Android",
                "PHP Web", "Ruby on Rails", "React.js Frontend", "Angular Framework", "Vue.js Development",
                "HTML5 & CSS3", "AWS Cloud Services", "Microsoft Azure", "Google Cloud Platform (GCP)",
                "DevOps Engineering", "Docker Containerization", "Kubernetes Orchestration", "CI/CD Pipelines",
                "Terraform (IaC)", "Linux System Administration", "Machine Learning", "Deep Learning",
                "Natural Language Processing (NLP)", "Computer Vision", "Data Science & Analytics",
                "Big Data (Hadoop/Spark)", "Generative AI & LLMs", "MLOps", "SQL Database", "NoSQL Solutions",
                "Cybersecurity Fundamentals", "Ethical Hacking", "Network Security", "Cloud Security",
                "Penetration Testing", "Blockchain & Web3", "Internet of Things (IoT)",
                "Game Development (Unity/Unreal)", "Embedded Systems", "Robotics & Automation",
                "AR/VR Development", "UI/UX Design", "Mobile App Development", "Software Testing (QA)",
                "Agile/Scrum Methodologies", "Computer Architecture"
            };
        }

        public async Task<CourseDTO> GenerateCourseFromExternalApiAsync(string category, DifficultyLevel difficulty, CancellationToken cancellationToken)
        {
            var apiKey = _config["Gemini:ApiKey"];
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-pro:generateContent?key={apiKey}";

            var prompt = $"Generate a professional technical course for '{category}' at '{difficulty}' level. " +
                         "Return ONLY a raw JSON object with 'Title', 'Description', and an array 'Topics' containing 10 specific sub-topic names. " +
                         "Do not use markdown.";

            var requestBody = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };

            try
            {
                var response = await _httpClient.PostAsJsonAsync(url, requestBody, cancellationToken);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                var aiText = result.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

                var cleanedJson = System.Text.RegularExpressions.Regex.Replace(aiText ?? "{}", @"^```json|```$", "", System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
                var aiData = JsonSerializer.Deserialize<GoogleCourseModel>(cleanedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var course = new Course
                {
                    Id = Guid.NewGuid(),
                    CourseName = aiData?.Title ?? $"{category} {difficulty} Track",
                    Description = aiData?.Description ?? $"Mastery curriculum for {category}."
                };

                await _courseRepository.AddCourseAsync(course);

                if (aiData?.Topics != null)
                {
                    foreach (var topicName in aiData.Topics)
                    {
                        await _languageRepository.AddLanguageAsync(new Language
                        {
                            Id = Guid.NewGuid(),
                            LanguageName = topicName,
                            Description = $"Assessment module for {topicName}.",
                            CourseId = course.Id
                        });
                    }
                }

                await _courseRepository.SaveAsync();
                return new CourseDTO { Id = course.Id, CourseName = course.CourseName };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini generation failed for {Category}. Creating basic node.", category);
                var fallbackCourse = new Course { Id = Guid.NewGuid(), CourseName = category, Description = "Node generated in offline mode." };
                await _courseRepository.AddCourseAsync(fallbackCourse);
                await _courseRepository.SaveAsync();

                return new CourseDTO { Id = fallbackCourse.Id, CourseName = fallbackCourse.CourseName };
            }
        }

        public async Task<IEnumerable<CourseDTO>> GetAllCoursesAsync(CancellationToken cancellationToken)
        {
            var courses = await _courseRepository.GetAllCoursesAsync();
            return courses.Select(c => new CourseDTO { Id = c.Id, CourseName = c.CourseName });
        }

        public async Task<CourseDTO?> GetCourseByIdAsync(Guid courseId, CancellationToken cancellationToken)
        {
            var c = await _courseRepository.GetCourseByIdAsync(courseId);
            return c == null ? null : new CourseDTO { Id = c.Id, CourseName = c.CourseName };
        }

        public async Task<bool> UpdateCourseAsync(Guid id, CourseDTO courseDto, CancellationToken cancellation)
        {
            var course = await _courseRepository.GetCourseByIdAsync(id);
            if (course == null) return false;

            course.CourseName = courseDto.CourseName;
            await _courseRepository.SaveAsync();
            return true;
        }

        //private async Task<string> GetLightcastTokenAsync()
        //{
        //    const string cacheKey = "Lightcast_AccessToken";
        //    if (_cache.TryGetValue(cacheKey, out string? token) && !string.IsNullOrWhiteSpace(token)) return token;

        //    var body = $"client_id={_clientId}&client_secret={_clientSecret}&grant_type=client_credentials&scope=emsi_open";
        //    var content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        //    var response = await _httpClient.PostAsync("https://emsicloud.com", content);
        //    response.EnsureSuccessStatusCode();

        //    var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
        //    var accessToken = result?.access_token ?? "";

        //    _cache.Set(cacheKey, accessToken, TimeSpan.FromSeconds((result?.expires_in ?? 3600) - 60));
        //    return accessToken;
        //}

        //private List<string> GetFallbackCategories()
        //{
        //    return new List<string>
        //    {
        //        "Python Development", "Java Enterprise", "C# .NET", "C++ Systems", "C Programming",
        //        "JavaScript/TypeScript", "Go (Golang)", "Rust Systems", "PHP Web", "Ruby on Rails",
        //        "React.js Frontend", "Angular Framework", "Vue.js Development", "HTML5 & CSS3",
        //        "AWS Cloud Services", "Microsoft Azure", "Google Cloud Platform (GCP)",
        //        "DevOps Engineering", "Linux System Administration", "Machine Learning",
        //        "Deep Learning", "Generative AI & LLMs", "Data Science & Analytics",
        //        "SQL Database", "Cybersecurity Fundamentals", "Ethical Hacking",
        //        "Blockchain & Web3", "Swift iOS", "Kotlin Android", "IoT Engineering"
        //    }.OrderBy(x => x).ToList();
        //}

        private class TokenResponse { public string access_token { get; set; } = ""; public int expires_in { get; set; } }
        private class GoogleCourseModel { public string? Title { get; set; } public string? Description { get; set; } public List<string>? Topics { get; set; } }
    }
}
