using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface IValidationService
{
    ValidationResult Validate(PackagingRequest request);
}
