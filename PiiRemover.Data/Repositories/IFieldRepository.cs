using PiiRemover.Core.Models;

namespace PiiRemover.Data.Repositories;

public interface IFieldRepository
{
    Task<IEnumerable<PiiField>> GetFieldsWithPatternsAsync(int? clientId);
    Task<IEnumerable<PiiField>> GetAllFieldsAsync();
    Task<int> CreateFieldAsync(int? clientId, string fieldName, string replaceWith);
    Task SetFieldActiveAsync(int fieldId, bool active);
    Task DeleteFieldAsync(int fieldId);

    Task<int> CreatePatternAsync(int fieldId, PatternType type, string pattern, int priority);
    Task UpdatePatternAsync(int patternId, PatternType type, string pattern, int priority);
    Task DeletePatternAsync(int patternId);
}
