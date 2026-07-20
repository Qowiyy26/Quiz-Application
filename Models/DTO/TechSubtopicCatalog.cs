namespace Quiz_Application.Models.DTO
{
    public static class TechSubtopicCatalog
    {
        public static List<string> GetSubtopics(string? languageName)
        {
            return GetSubtopics(languageName, "Architecture");
        }

        public static List<string> GetSubtopics(string? languageName, string? track)
        {
            var key = (languageName ?? string.Empty).Trim().ToLowerInvariant();
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["c# .net"] = new() { "C# syntax", "OOP", "LINQ", "ASP.NET Core MVC", "Web API", "Entity Framework Core", "dependency injection", "async/await", "middleware", "authentication", "validation", "debugging", "unit testing" },
                ["asp.net core mvc"] = new() { "controllers", "actions", "routing", "views", "models", "Razor syntax", "model binding", "validation", "Entity Framework Core", "authentication", "dependency injection", "middleware" },
                ["php"] = new() { "PHP syntax", "forms", "sessions", "arrays", "functions", "OOP", "PDO database access", "Laravel basics", "validation", "authentication", "security", "file handling" },
                ["python"] = new() { "Python syntax", "functions", "lists and dictionaries", "OOP", "exceptions", "file handling", "modules", "virtual environments", "Django basics", "FastAPI basics", "testing", "debugging" },
                ["java"] = new() { "Java syntax", "OOP", "collections", "exceptions", "streams", "Spring Boot", "JPA/Hibernate", "REST APIs", "validation", "testing", "debugging" },
                ["javascript"] = new() { "JavaScript syntax", "DOM", "functions", "arrays", "objects", "promises", "async/await", "events", "fetch API", "Node.js", "debugging", "browser behavior" },
                ["typescript"] = new() { "types", "interfaces", "generics", "classes", "modules", "async logic", "type narrowing", "Node.js", "frontend integration", "debugging" },
                ["c++"] = new() { "syntax", "pointers", "references", "OOP", "STL containers", "memory management", "templates", "exceptions", "performance", "debugging" },
                ["c programming"] = new() { "syntax", "pointers", "arrays", "strings", "structs", "memory allocation", "file I/O", "preprocessor", "debugging", "data structures" },
                ["html5"] = new() { "semantic HTML", "forms", "tables", "media", "accessibility", "SEO basics", "validation", "page structure" },
                ["css3"] = new() { "selectors", "box model", "flexbox", "grid", "responsive design", "variables", "animations", "specificity", "layout debugging" },
                ["react.js"] = new() { "components", "props", "state", "hooks", "events", "forms", "routing", "API calls", "rendering", "performance", "testing" },
                ["angular"] = new() { "components", "templates", "services", "dependency injection", "routing", "forms", "RxJS", "HTTP client", "guards", "testing" },
                ["vue.js"] = new() { "components", "reactivity", "props", "events", "composition API", "routing", "Pinia", "forms", "API calls" },
                ["node.js"] = new() { "runtime", "modules", "npm", "Express", "routing", "middleware", "REST APIs", "authentication", "database access", "error handling" },
                ["sql database"] = new() { "SELECT queries", "joins", "filtering", "aggregation", "indexes", "normalization", "transactions", "stored procedures", "query optimization" },
                ["mysql"] = new() { "tables", "relationships", "SELECT queries", "joins", "indexes", "constraints", "transactions", "stored procedures", "backup basics" },
                ["postgresql"] = new() { "schemas", "constraints", "joins", "indexes", "transactions", "views", "stored functions", "query plans", "backup and restore" },
                ["rest api design"] = new() { "HTTP methods", "status codes", "routing", "request validation", "authentication", "versioning", "pagination", "error responses", "API security" },
                ["web security"] = new() { "XSS", "CSRF", "SQL injection", "authentication", "authorization", "secure cookies", "input validation", "HTTPS", "OWASP basics" },
                ["authentication & authorization"] = new() { "login flow", "password hashing", "roles", "claims", "JWT", "cookies", "sessions", "access control", "least privilege" },
                ["network security"] = new() { "firewalls", "VPNs", "TLS", "DNS security", "IDS/IPS", "Wireshark", "network attacks", "hardening", "monitoring" },
                ["cybersecurity fundamentals"] = new() { "CIA triad", "authentication", "authorization", "cryptography", "risk assessment", "malware", "incident response", "secure passwords" },
                ["aws cloud services"] = new() { "EC2", "S3", "IAM", "VPC", "Lambda", "RDS", "CloudFront", "security groups", "monitoring", "cost basics" },
                ["microsoft azure"] = new() { "Azure App Service", "Azure Functions", "storage accounts", "Entra ID", "Cosmos DB", "virtual networks", "monitoring", "deployment" },
                ["machine learning"] = new() { "supervised learning", "unsupervised learning", "features", "training", "testing", "metrics", "overfitting", "model evaluation", "scikit-learn" },
                ["artificial intelligence"] = new() { "AI concepts", "machine learning", "neural networks", "NLP", "computer vision", "model evaluation", "prompt engineering", "ethics" }
            };

            List<string> subtopics;
            if (map.TryGetValue(key, out var exactSubtopics))
            {
                subtopics = exactSubtopics;
                return FilterByTrack(subtopics, track);
            }

            foreach (var pair in map)
            {
                if (key.Contains(pair.Key) || pair.Key.Contains(key))
                {
                    subtopics = pair.Value;
                    return FilterByTrack(subtopics, track);
                }
            }

            subtopics = new List<string>
            {
                "core concepts", "basic syntax or terminology", "practical usage", "debugging",
                "security", "best practices", "testing", "performance", "real-world scenarios"
            };
            return FilterByTrack(subtopics, track);
        }

        public static string ClassifyTrack(string subtopic)
        {
            var value = (subtopic ?? string.Empty).Trim().ToLowerInvariant();
            var professionalKeywords = new[]
            {
                "architecture", "middleware", "dependency injection", "authentication", "authorization",
                "security", "performance", "optimization", "query plans", "indexes", "transactions",
                "testing", "unit testing", "debugging", "deployment", "monitoring", "scalability",
                "guards", "rxjs", "jpa", "hibernate", "spring boot", "laravel", "django", "fastapi",
                "web api", "rest api", "api security", "cookies", "sessions", "jwt", "iam", "vpc",
                "lambda", "cloudfront", "security groups", "overfitting", "model evaluation"
            };

            var beginnerKeywords = new[]
            {
                "syntax", "basics", "basic", "core concepts", "terminology", "forms", "arrays",
                "functions", "objects", "classes", "models", "views", "controllers", "routing",
                "tables", "select queries", "filtering", "semantic html", "box model", "components",
                "props", "state", "runtime", "modules", "npm", "ec2", "s3", "ai concepts"
            };

            if (professionalKeywords.Any(value.Contains))
            {
                return "Professional";
            }

            if (beginnerKeywords.Any(value.Contains))
            {
                return "Fundamentals";
            }

            return value.Length <= 18 ? "Fundamentals" : "Professional";
        }

        private static List<string> FilterByTrack(List<string> subtopics, string? track)
        {
            var selectedTrack = (track ?? "Architecture").Trim();
            if (selectedTrack.Equals("Architecture", StringComparison.OrdinalIgnoreCase))
            {
                return subtopics;
            }

            var filtered = subtopics
                .Where(s => ClassifyTrack(s).Equals(selectedTrack, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return filtered.Any() ? filtered : subtopics;
        }
    }
}
