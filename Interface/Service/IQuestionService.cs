using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Quiz_Application.Models.DTO.Question;

namespace Quiz_Application.Interface.Service
{
    public interface IQuestionService
    {
        Task<IEnumerable<QuestionDTO>> GetQuestionsByQuizIdAsync(Guid quizId, CancellationToken cancellation);
        Task<QuestionDTO?> GetQuestionByIdAsync(Guid questionId, CancellationToken cancellation);
        Task<List<QuestionDTO>> GenerateQuestionsFromApiAsync(Guid languageId, string level, int numberOfQuestions, Guid userId, IEnumerable<string>? selectedSubtopics, string? subtopicTrack, CancellationToken cancellation);
        Task<bool> SaveQuestionsAsync(Guid quizId, List<QuestionDTO> questions, CancellationToken cancellation);
        Task<bool> DeleteQuestionAsync(Guid questionId, CancellationToken cancellation);
        Task<IEnumerable<QuestionDTO>> GetQuestionsForUserAsync(int requestedCount, CancellationToken cancellation);
    }
}

