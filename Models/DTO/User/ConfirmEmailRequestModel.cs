using System.ComponentModel.DataAnnotations;

namespace Quiz_Application.Models.DTO.User
{
    public class ConfirmEmailRequestModel
    {
        [Required]
        [EmailAddress]
        public string? Email { get; set; }

        [Required]
        [StringLength(6, MinimumLength = 6)]
        public string? Code { get; set; }
    }
}
