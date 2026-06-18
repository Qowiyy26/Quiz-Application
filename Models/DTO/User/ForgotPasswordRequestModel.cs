using System.ComponentModel.DataAnnotations;

namespace Quiz_Application.Models.DTO.User
{
    public class ForgotPasswordRequestModel
    {
        [Required]
        [EmailAddress]
        public string? Email { get; set; }
    }
}
