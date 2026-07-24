namespace Acme.ChatSocial.Domain;

public class GroupMember
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? Avatar { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsOwner => Role == "Owner";
}