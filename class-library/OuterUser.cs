namespace class_library;

public class OuterUser
{
    public OuterUser(string username, Chat chat, string? name)
    {
        Id = username;
        var user = chat.Members.FirstOrDefault(x => x.Username == username);
        if (user == null)
        {
            throw new Exception("User not found in chat");
        }

        // If name provided is null, use user display name
        Name = name ?? user.Name;
        Server = user.Server == "localhost" ? "localhost:54321" : user.Server;
        // Get last message the user sent
        var lastMessage = chat.Messages.Where(x => x.Sender == username).OrderByDescending(x => x.Timestamp)
            .FirstOrDefault();
        if (lastMessage != null)
        {
            Last = lastMessage.Text;
            LastDate = lastMessage.Timestamp.ToString("MM/dd/yyyy, HH:mm:ss");
        }
    }

    public string Id { get; }
    public string Name { get; }
    public string Server { get; }
    public string? Last { get; }
    public string? LastDate { get; }
}