using Microsoft.Extensions.Caching.Memory;
using Quiz_Application.Interface.Repository;
using Quiz_Application.Interface.Service;
using Quiz_Application.Models;
using Quiz_Application.Models.DTO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Quiz_Application.Implementation.Service
{
    public class LanguageService : ILanguageService
    {
        private readonly ILanguageRepository _languageRepository;
        private readonly ICourseRepository _courseRepository;
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly ILogger<LanguageService> _logger;

        private const string CACHE_KEY_TOKEN = "Lightcast_AccessToken";
        private const string CACHE_KEY_TAGS = "Neural_External_Tags";

        public LanguageService(
            ILanguageRepository languageRepository,
            ICourseRepository courseRepository,
            HttpClient httpClient,
            IConfiguration config,
            IMemoryCache cache,
            ILogger<LanguageService> logger)
        {
            _languageRepository = languageRepository;
            _courseRepository = courseRepository;
            _httpClient = httpClient;
            _config = config;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IEnumerable<LanguageDTO>> GenerateLanguagesFromApiAsync(string courseCategory, CancellationToken cancellation)
        {
            try
            {
                var course = await _courseRepository.GetCourseByNameAsync(courseCategory);
                if (course == null) return Enumerable.Empty<LanguageDTO>();

                return await GenerateLanguagesForCourseAsync(course.Id, courseCategory, cancellation);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Manual Neural Data Injection Failure for category {Category}", courseCategory);
                return Enumerable.Empty<LanguageDTO>();
            }
        }

        public async Task<IEnumerable<LanguageDTO>> GenerateLanguagesForCourseAsync(Guid courseId, string courseCategory, CancellationToken cancellation)
        {
            try
            {
                var manualMatrix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "Web Application", new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "HTML5", "CSS3", "ASP.NET Core MVC", "Laravel", "Django", "Node.js", "React.js", "Angular", "Vue.js", "SQL Database", "MySQL", "REST API Design", "Web Security", "Authentication & Authorization" } },
            { "Web Application Development", new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "HTML5", "CSS3", "ASP.NET Core MVC", "Laravel", "Django", "Node.js", "React.js", "Angular", "Vue.js", "SQL Database", "MySQL", "REST API Design", "Web Security", "Authentication & Authorization" } },
            { "Web Development", new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "HTML5", "CSS3", "ASP.NET Core MVC", "Laravel", "Django", "Node.js", "React.js", "Angular", "Vue.js", "SQL Database", "MySQL", "REST API Design", "Web Security", "Authentication & Authorization" } },
            { "Programming Languages", new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "Go", "Rust", "Swift", "Kotlin", "Ruby", "SQL", "Object-Oriented Programming", "Data Structures", "Algorithms" } },
            { "Software Development", new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "Go", "Rust", "SQL Database", "Git Version Control", "Testing & QA", "Software Architecture", "API Design" } },
            { "Networking", new List<string> { "Computer Networking", "TCP/IP", "Routing & Switching", "DNS", "DHCP", "VLANs", "Subnetting", "Network Security", "Wireless Networking", "Network Troubleshooting", "Firewalls", "VPN & Tunneling" } },
            { "Artificial Intelligence", new List<string> { "Python", "Machine Learning", "Deep Learning", "Natural Language Processing", "Computer Vision", "Generative AI", "Neural Networks", "Model Evaluation", "Prompt Engineering", "Data Science", "Pandas & NumPy", "TensorFlow", "PyTorch" } },
            { "Database Systems", new List<string> { "SQL Database", "MySQL", "PostgreSQL", "SQL Server", "MongoDB", "Redis", "Database Design", "Query Optimization", "Indexing Strategies", "Stored Procedures", "Transactions", "NoSQL Solutions" } },
            { "Cloud Computing", new List<string> { "AWS Cloud Services", "Microsoft Azure", "Google Cloud Platform", "Cloud Security", "Docker", "Kubernetes", "Serverless Functions", "Cloud Databases", "CI/CD Pipelines", "Infrastructure as Code", "Cloud Networking", "Monitoring & Logging" } },

            { "Python Development", new List<string> { "Django & FastAPI", "Pandas & NumPy", "Asyncio Concurrency", "PyTest & Mocking", "Metaprogramming", "Type Hinting", "Generators & Iterators", "Multi-threading vs Multi-processing" } },
            { "Java Enterprise", new List<string> { "Spring Boot 3", "Hibernate/JPA Logic", "JVM Memory Management", "Microservices Architecture", "Kafka Integration", "Spring Security", "Maven/Gradle Build", "Garbage Collection Tuning" } },
            { "C# .NET", new List<string> { "ASP.NET Core Web API", "Entity Framework Core","ASP.NET(MVC)", "LINQ & Data Structures", "SignalR Real-time", "MAUI Mobile", "Dependency Injection", "Asynchronous Patterns (Task/ValueTask)", "Middleware Pipelines" } },
            { "JavaScript/TypeScript", new List<string> { "TypeScript Generics", "Node.js Event Loop", "ESNext Features", "V8 Engine Optimization", "Design Patterns", "Asynchronous Logic (Promises/Await)", "Event-Driven Architecture" } },
            { "Go (Golang)", new List<string> { "Goroutines & Channels", "Interfaces & Composition", "Context & Profiling", "GRPC Framework", "Memory Management", "Standard Library Internals", "Concurrency Patterns" } },
            { "Rust Systems", new List<string> { "Ownership & Borrowing", "Smart Pointers", "Fearless Concurrency", "Macros & Metaprogramming", "Cargo Tooling", "Lifetimes & Scopes", "Unsafe Rust Protocols" } },
            { "C++ Systems", new List<string> { "STL Containers", "Template Metaprogramming", "Memory Management (RAII)", "Multithreading", "C++20 Modules", "Move Semantics", "Pointer Arithmetic" } },
            { "C Programming", new List<string> { "Pointer Arithmetic", "Dynamic Memory (malloc/free)", "Custom Data Structures", "File I/O Streams", "Pre-processor Macros", "Bitwise Manipulation", "System Calls (POSIX)" } },
            { "PHP Web", new List<string> { "Laravel Framework", "Symfony Components", "PHP 8.x Attributes", "Composer Workflow", "Unit Testing", "Eloquent ORM Logic", "MVC Architecture" } },
            { "Ruby on Rails", new List<string> { "Active Record Logic", "ActionController Patterns", "Hotwire & Turbo", "RSpec Testing", "Metaprogramming Ruby", "Sidekiq Background Jobs" } },

            { "React.js Frontend", new List<string> { "React Hooks API", "Next.js 14 App Router", "State Management (Redux/Zustand)", "Component Architecture", "Performance Profiling", "Server Components (RSC)", "Client-side Routing" } },
            { "Angular Framework", new List<string> { "RxJS Observables", "Angular Signals", "Dependency Injection", "Directive Logic", "Zone.js Performance", "NgModule vs Standalone", "Interceptors" } },
            { "Vue.js Development", new List<string> { "Composition API", "Pinia Store", "Vue Router Guard", "Nuxt.js Integration", "Vite Tooling", "Virtual DOM Internals" } },
            { "HTML5 & CSS3", new List<string> { "Flexbox & Grid Layouts", "CSS Variables & Themes", "Web Accessibility (A11y)", "Canvas & SVG Animation", "Browser Rendering Path", "Sass/SCSS Preprocessing" } },

            { "AWS Cloud Services", new List<string> { "Serverless (Lambda)", "IAM & Security Policy", "RDS & DynamoDB Clusters", "CloudFormation/CDK", "VPC Networking", "S3 & CloudFront", "SQS & SNS Messaging" } },
            { "Microsoft Azure", new List<string> { "Azure Functions", "Entra ID (Active Directory)", "Cosmos DB NoSQL", "Logic Apps Workflow", "AKS Orchestration", "Azure Devops Pipelines" } },
            { "Google Cloud Platform (GCP)", new List<string> { "Compute Engine", "BigQuery Analytics", "App Engine", "Cloud Run (Containers)", "Pub/Sub Messaging", "Cloud Spanner Architecture" } },
            { "DevOps Engineering", new List<string> { "Docker Containerization", "Kubernetes (K8s)", "Terraform IaC", "Jenkins/GitHub Actions", "Ansible Configuration", "CI/CD Pipeline Design", "Monitoring (Prometheus/Grafana)" } },
            { "Linux System Administration", new List<string> { "Bash Scripting", "Process Management", "Network Protocols", "Kernel Hardening", "SSH Security", "Systemd Services", "LVM & Disk Management" } },

            { "SQL Database", new List<string> { "Query Optimization", "Indexing Strategies", "Stored Procedures", "Triggers & Events", "Normalisation (1NF-3NF)", "Window Functions", "Transaction ACID Properties" } },
            { "NoSQL Solutions", new List<string> { "MongoDB Aggregation", "Redis Caching Patterns", "Cassandra Partitioning", "Elasticsearch Logic", "Document vs Graph DBs", "Consistency Models" } },
            { "Big Data", new List<string> { "Apache Spark Streaming", "Hadoop Ecosystem", "Data Lakes & Warehousing", "ETL Process Design", "Hive Query Language", "Airflow Scheduling" } },

            { "Machine Learning", new List<string> { "Supervised Learning", "Model Evaluation Metrics", "Feature Selection", "Gradient Descent", "Scikit-Learn Workflow", "Ensemble Methods", "Hyperparameter Tuning" } },
            { "Deep Learning", new List<string> { "Neural Networks (CNN/RNN)", "PyTorch Framework", "TensorFlow Architecture", "Computer Vision Basics", "GPU Acceleration", "Backpropagation Math" } },
            { "Natural Language Processing (NLP)", new List<string> { "Tokenization & Embeddings", "Transformer Models", "Sentiment Analysis", "BERT & GPT Fine-tuning", "Regex Patterns", "Named Entity Recognition" } },
            { "Data Science & Analytics", new List<string> { "Exploratory Data Analysis", "Statistical Significance", "SQL Window Functions", "Power BI / Tableau", "Data ETL Pipelines", "Probability Theory" } },
            { "Generative AI & LLMs", new List<string> { "Prompt Engineering", "Vector Databases (Pinecone)", "LangChain Integration", "RAG Architecture", "Model Quantization", "Attention Mechanisms" } },

            { "Ethical Hacking", new List<string> { "Penetration Testing", "Metasploit Pro", "Kali Linux Tooling", "Buffer Overflows", "Vulnerability Scanning", "Network Sniffing", "Social Engineering Protocols" } },
            { "Cybersecurity Fundamentals", new List<string> { "Cryptography Standards", "Zero Trust Architecture", "SOC Analysis", "Incident Response", "Compliance (GDPR/ISO)", "Risk Assessment", "Firewall Hardening" } },
            { "Network Security", new List<string> { "Firewall Config (WAF)", "VPN & Tunneling", "Wireshark Analysis", "IDPS Logic", "DNSSEC Protocols", "TLS/SSL Handshakes" } },

            { "Swift iOS", new List<string> { "SwiftUI Declarative", "UIKit Architecture", "Core Data Persistence", "Combine Framework", "App Store Guidelines", "Memory Management (ARC)" } },
            { "Kotlin Android", new List<string> { "Jetpack Compose", "Coroutines & Flow", "Dagger Hilt DI", "Room Database", "Retrofit API", "MVVM Architecture" } },
            { "Blockchain & Web3", new List<string> { "Solidity Contracts", "Ethereum EVM", "IPFS Storage", "Web3.js/Ethers.js", "Hyperledger Fabric", "Smart Contract Auditing" } },
            { "Internet of Things (IoT)", new List<string> { "MQTT Messaging", "Embedded C/C++", "Raspberry Pi / Arduino", "Sensor Integration", "Edge Computing", "Real-Time OS (RTOS)" } },
            { "Game Development (Unity/Unreal)", new List<string> { "C# Scripting (Unity)", "C++ Physics (Unreal)", "Shader Graphs", "Animation State Machines", "Ray Tracing", "Multiplayer Sync Logic" } }
        };

                var selectedTopics = ResolveTopics(courseCategory, manualMatrix);
                var existingLanguages = await _languageRepository.GetLanguagesByCourseIdAsync(courseId);
                var existingNames = existingLanguages
                    .Select(l => l.LanguageName ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var createdLanguages = new List<LanguageDTO>();

                foreach (var skillName in selectedTopics)
                {
                    if (existingNames.Contains(skillName)) continue;

                    var language = new Language
                    {
                        Id = Guid.NewGuid(),
                        LanguageName = skillName,
                        Description = $"Assessment module for {skillName}.",
                        CourseId = courseId
                    };

                    await _languageRepository.AddLanguageAsync(language);
                    existingNames.Add(skillName);
                    createdLanguages.Add(new LanguageDTO
                    {
                        Id = language.Id,
                        LanguageName = language.LanguageName,
                        CourseId = language.CourseId
                    });
                }

                await _languageRepository.SaveAsync();
                return createdLanguages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual Neural Data Injection Failure for category {Category}", courseCategory);
                return Enumerable.Empty<LanguageDTO>();
            }
        }


        private static List<string> ResolveTopics(string courseCategory, Dictionary<string, List<string>> manualMatrix)
        {
            if (manualMatrix.TryGetValue(courseCategory, out var exactTopics))
            {
                return exactTopics;
            }

            var normalized = courseCategory.ToLowerInvariant();

            if (normalized.Contains("web") || normalized.Contains("application"))
            {
                return manualMatrix["Web Application"];
            }

            if (normalized.Contains("program") || normalized.Contains("language") || normalized.Contains("software"))
            {
                return manualMatrix["Programming Languages"];
            }

            if (normalized.Contains("database") || normalized.Contains("sql"))
            {
                return manualMatrix["Database Systems"];
            }

            if (normalized.Contains("cyber") || normalized.Contains("security"))
            {
                return new List<string> { "Cybersecurity Fundamentals", "Network Security", "Ethical Hacking", "Penetration Testing", "Cryptography", "Web Security", "SOC Analysis", "Incident Response", "Firewall Hardening", "Zero Trust Architecture" };
            }

            if (normalized.Contains("cloud"))
            {
                return manualMatrix["Cloud Computing"];
            }

            if (normalized.Contains("ai") || normalized.Contains("artificial") || normalized.Contains("machine") || normalized.Contains("data"))
            {
                return manualMatrix["Artificial Intelligence"];
            }

            if (normalized.Contains("network"))
            {
                return manualMatrix["Networking"];
            }

            return new List<string> { "C# .NET", "PHP", "C++", "C Programming", "Python", "Java", "JavaScript", "TypeScript", "SQL Database", "Web Security", "Testing & QA", "API Design" };
        }



        public async Task<List<string>> GetExternalTagsAsync()
        {
            if (_cache.TryGetValue(CACHE_KEY_TAGS, out List<string>? cachedTags) && cachedTags != null) return cachedTags;

            var tags = GetDefaultTechTags().OrderBy(x => x).ToList();
            _cache.Set(CACHE_KEY_TAGS, tags, TimeSpan.FromHours(12));
            return await Task.FromResult(tags);
        }

        private static List<string> GetDefaultTechTags()
        {
            return new List<string>
            {
                "Web Application", "Web Development", "Programming Languages", "Software Development",
                "Networking", "Artificial Intelligence", "Database Systems", "Cloud Computing",
                "Data Science", "DevOps", "Cybersecurity", "Mobile Development"
            };
        }


        public async Task<IEnumerable<LanguageDTO>> GetAllLanguagesAsync(CancellationToken ct)
        {
            var list = await _languageRepository.GetAllLanguagesAsync();
            return list.Select(l => new LanguageDTO { Id = l.Id, LanguageName = l.LanguageName, CourseId = l.CourseId });
        }

        public async Task<LanguageDTO?> GetLanguageByIdAsync(Guid languageId, CancellationToken ct)
        {
            var l = await _languageRepository.GetLanguageByIdAsync(languageId);
            return l == null ? null : new LanguageDTO { Id = l.Id, LanguageName = l.LanguageName, CourseId = l.CourseId };
        }

        public async Task<LanguageDTO?> GetLanguageByNameAsync(string languageName, CancellationToken ct)
        {
            var l = await _languageRepository.GetLanguageByNameAsync(languageName);
            return l == null ? null : new LanguageDTO { Id = l.Id, LanguageName = l.LanguageName, CourseId = l.CourseId };
        }

        public async Task<IEnumerable<LanguageDTO>> GetLanguagesByCourseAsync(string courseName, CancellationToken ct)
        {
            var list = await _languageRepository.GetLanguagesByCourseNameAsync(courseName);
            return list.Select(l => new LanguageDTO { Id = l.Id, LanguageName = l.LanguageName, CourseId = l.CourseId });
        }

        public async Task<IEnumerable<LanguageDTO>> GetLanguagesByCourseIdAsync(Guid courseId, CancellationToken ct)
        {
            var list = await _languageRepository.GetLanguagesByCourseIdAsync(courseId);
            return list.Select(l => new LanguageDTO { Id = l.Id, LanguageName = l.LanguageName, CourseId = l.CourseId });
        }

        private async Task<string> GetLightcastTokenAsync()
        {
            if (_cache.TryGetValue(CACHE_KEY_TOKEN, out string? token) && !string.IsNullOrWhiteSpace(token)) return token;

            var body = $"client_id={_config["Lightcast:clientId"]}&client_secret={_config["Lightcast:clientSecret"]}&grant_type=client_credentials&scope=emsi_open";
            var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

            var response = await _httpClient.PostAsync("https://auth.emsicloud.com/connect/token", content);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TokenResponse>();
            var accessToken = result?.access_token ?? "";

            _cache.Set(CACHE_KEY_TOKEN, accessToken, TimeSpan.FromSeconds((result?.expires_in ?? 3600) - 60));
            return accessToken;
        }

        private class TokenResponse { public string access_token { get; set; } = ""; public int expires_in { get; set; } }
    }
}
