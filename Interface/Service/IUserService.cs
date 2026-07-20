using Quiz_Application.Models.DTO.User;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Quiz_Application.Interface.Service
{
    public interface IUserService
    {
        Task<CreateUserRequestModel> CreateUserAsync(CreateUserRequestModel request, CancellationToken cancellationToken );
        Task<LoginResponseModel> LoginAsync(LoginRequestModel request, CancellationToken cancellationToken );
        Task<UserDTO?> GetUserByEmail(string email, CancellationToken cancellationToken);
        Task<UserDTO?> GetUserProfileByUserId(Guid userId, CancellationToken cancellationToken);
        Task<int> GetAllUsersCount(CancellationToken cancellationToken );
    }
}


