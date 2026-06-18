using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Quiz_Application.Models.DTO;

namespace Quiz_Application.Interface.Service
{
    public interface IQuizService
    {
        
        Task<QuizDTO> GenerateQuizAsync(Guid userId, Guid languageId, string level, int questionCount, IEnumerable<string>? selectedSubtopics, CancellationToken cancellation);
        Task<ResultDTO> SubmitQuizAsync(Guid quizId, Dictionary<Guid, string> userAnswers, CancellationToken cancellation);
        Task<string?> GetQuizResultAsync(Guid quizId, CancellationToken cancellationToken);
        Task<QuizDTO?> GetQuizByIdAsync(Guid quizId, CancellationToken cancellation);
        Task<QuizDTO?> GetQuizReviewAsync(Guid quizId, CancellationToken cancellation);
        Task<IEnumerable<QuizDTO>> GetQuizzesByUserIdAsync(Guid userId, CancellationToken cancellation);
        Task<string?> GetAllQuizzesAsync(CancellationToken cancellation);
    }

}

