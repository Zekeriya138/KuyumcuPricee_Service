namespace KUYUMCU.Price_Service.Models
{
    public record CategoryDto(Guid Id, string Name, string KategoriKodu);

    public record CreateCategoryDto(string Name, string KategoriKodu);

    public record UpdateCategoryDto(string Name, string KategoriKodu);
}
