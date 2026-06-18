using System.ComponentModel.DataAnnotations;

namespace Quiz_Application.Models.DTO.User
{
    public class ResetPasswordRequestModel
    {
        [Required]
        [EmailAddress]
        public string? Email { get; set; }

        [Required]
        [StringLength(6, MinimumLength = 6)]
        public string? Code { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [MinLength(8)]
        public string? NewPassword { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [Compare(nameof(NewPassword))]
        public string? ConfirmPassword { get; set; }
    }
}
