namespace Acme.ChatSocial.Domain;

public class Group
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerUsername { get; set; } = string.Empty;
    public int MemberCount { get; set; }
}