using Microsoft.EntityFrameworkCore;
using Quiz_Application.DBCONTEXT;
using Quiz_Application.Interface.Repository;
using Quiz_Application.Models;
using Quiz_Application.Models.BaseEntities;

namespace Quiz_Application.Implementation.Repository
{
    public class LanguageRepository : ILanguageRepository
    {
        private readonly QuizContext _quizContext;

        public LanguageRepository(QuizContext quizContext)
        {
            _quizContext = quizContext;
        }

        public async Task AddLanguageAsync(Language language)
        {
            await _quizContext.Languages.AddAsync(language);
            await SaveAsync(); 
        }

        public async Task<IEnumerable<Language>> GetAllLanguagesAsync()
        {
            return await _quizContext.Languages.AsNoTracking().ToListAsync();
        }

        public async Task<Language?> GetLanguageByIdAsync(Guid languageId)
        {
            return await _quizContext.Languages
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == languageId);
        }

        public async Task<Language?> GetLanguageByNameAsync(string languageName)
        {
            return await _quizContext.Languages
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.LanguageName == languageName);
        }

        public async Task<IEnumerable<Language>> GetLanguagesByCourseNameAsync(string courseName)
        {
            return await _quizContext.Languages
                .Where(l => l.Course != null && l.Course.CourseName == courseName)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task<IEnumerable<Language>> GetLanguagesByCourseIdAsync(Guid courseId)
        {
            return await _quizContext.Languages
                .Where(l => l.CourseId == courseId)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task SaveAsync()
        {
            await _quizContext.SaveChangesAsync();
        }
    }
}
