using Quiz_Application.Models.DTO.Question;

namespace Quiz_Application.Models.DTO
{
    public class QuizDTO
    {
        public Guid Id { get; set; }
        public string? UserName { get; set; }
        public string? CourseName { get; set; }
        public string? LanguageName { get; set; }
        public string? Level { get; set; }
        public string QuizMode { get; set; } = "Standard";
        public int TotalQuestions { get; set; }
        public int DurationMinutes { get; set; } = 20;
        public int Score { get; set; }
        public List<QuestionDTO> Questions { get; set; } = new List<QuestionDTO>();
        public DateTime CreatedDate { get; set; }
        public object Title { get; internal set; } = string.Empty;
    }
}
