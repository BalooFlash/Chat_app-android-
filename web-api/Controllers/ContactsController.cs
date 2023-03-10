using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using class_library;
using class_library.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using web_api.Hubs;
using JwtRegisteredClaimNames = Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames;

namespace web_api.Controllers;

[ApiController]
[Route("api/contacts")]
public class ContactsController : ControllerBase
{
    private readonly IUsersService _usersService;
    private readonly IChatsService _chatsService;
    private readonly IConfiguration _configuration;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly HttpClient _httpClient = new HttpClient();
    private readonly Sender _sender;
    private Dictionary<String, String> _tokens;
    private IHubContext<MessageHub> _hubContext;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContactsController(IUsersService usersService, IChatsService chatsService, IConfiguration configuration,
        Sender sender, Dictionary<String, String> tokens,IHubContext<MessageHub> hubContext)
    {
        _usersService = usersService;
        _chatsService = chatsService;
        _configuration = configuration;
        _tokenHandler = new JwtSecurityTokenHandler();
        _sender = sender;
        _tokens = tokens;
        _hubContext = hubContext;
    }

    private User? _getCurrentUser()
    {
        var token = _tokenHandler.ReadJwtToken(Request.Headers["Authorization"].ToString().Substring("Bearer ".Length));
        return _usersService.Get(token.Claims.First(claim => claim.Type == "username").Value);
    }

    private string _createJwtToken(string username)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, _configuration["JWTParams:Subject"]),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTime.UtcNow.ToString()),
            new Claim("username", username)
        };
        var secretKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWTParams:SecretKey"]));
        var mac = new SigningCredentials(secretKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _configuration["JWTParams:Issuer"], _configuration["JWTParams:Audience"], claims,
            expires: DateTime.UtcNow.AddMinutes(20), signingCredentials: mac);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpPost("signin")]
    public IActionResult SignIn([FromBody] JsonElement body)
    {
        // Sign in user

        string? username, password;
        try
        {
            username = body.GetProperty("username").GetString();
            password = body.GetProperty("password").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (username == null || password == null)
        {
            return BadRequest();
        }

        var user = _usersService.Get(username);
        if (user == null || user.Server != "localhost" || user.Password != password)
        {
            return Unauthorized();
        }

        // Return JWT token
        var token = _createJwtToken(username);
        var name = user.Name;
        try
        {
            string? firebaseToken;
            firebaseToken = body.GetProperty("firebaseToken").GetString();
            _tokens.Add(username, firebaseToken);
        }
        catch
        {
        }

        return Ok(new { token, name });
    }

    [HttpPost("signup")]
    public IActionResult SignUp([FromBody] JsonElement body)
    {
        // Sign up new user

        string? username, password, name;
        try
        {
            username = body.GetProperty("username").GetString();
            password = body.GetProperty("password").GetString();
            name = body.GetProperty("name").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (username == null || password == null || name == null)
        {
            return BadRequest();
        }

        // Check regex for username
        if (username.Length < 3 || !Regex.IsMatch(username, @"^[a-zA-Z0-9-]+$"))
        {
            return BadRequest();
        }


        // Check if password contains at least one number, one lowercase and one uppercase character
        if (password.Length < 6 || !Regex.IsMatch(password, @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).+$"))
        {
            return BadRequest();
        }

        // Check regex for name (display name)
        if (name.Length < 3 || !Regex.IsMatch(name, @"^[a-zA-Z '\-.,]+$"))
        {
            return BadRequest();
        }

        if (_usersService.Get(username) != null)
        {
            return BadRequest();
        }

        string? profilePicture;
        try
        {
            profilePicture = body.GetProperty("profilePicture").GetString();
        }
        catch
        {
            byte[] imageBytes = System.IO.File.ReadAllBytes("photos/profilePicture.png");
            profilePicture = Convert.ToBase64String(imageBytes);
        }

        // Create new user
        var newUser = new User(username, name, "localhost", password, profilePicture);
        _usersService.Add(newUser);

        return Created("", null);
    }


    [HttpGet]
    [Authorize]
    public IActionResult Get()
    {
        // Get all contacts of the current user

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        IEnumerable<User> contacts = _usersService.Get(currentUser.ContactsIds);
        // Loop over contacts and create a list of outerUsers from them
        List<OuterUser> outerContacts = new List<OuterUser>();
        foreach (User c in contacts)
        {
            string? displayName = currentUser.ContactsNames[currentUser.ContactsIds.IndexOf(c.Username)];
            outerContacts.Add(new OuterUser(c.Username, currentUser.Chats[currentUser.ContactsIds.IndexOf(c.Username)], displayName));
        }

        return Ok(outerContacts);
    }

    [HttpPost]
    [Authorize]
    public IActionResult Post([FromBody] JsonElement body)
    {
        // Add contact to the current user

        string? id, name, server;
        try
        {
            id = body.GetProperty("id").GetString();
            name = body.GetProperty("name").GetString();
            server = body.GetProperty("server").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (id == null || name == null || server == null)
        {
            return BadRequest();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest("Can't add yourself");
        }

        // If the contact is already in the current user's contacts, return BadRequest
        if (currentUser.ContactsIds.Contains(id))
        {
            return BadRequest("User is already in contacts list");
        }

        // Send an invitation first, to see if the remote server exists
        if (server != "localhost" && server != "localhost:54321")
        {
            // Send invitation to the contact on the remote server
            var invitation = new Invitation(currentUser.Username, id, "localhost:54321");
            var json = JsonSerializer.Serialize(invitation, _jsonSerializerOptions);
            var stringContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            try
            {
                _httpClient.PostAsync("https://" + server + "/api/invitations", stringContent).Wait();
            }
            catch (Exception)
            {
                return BadRequest("Couldn't communicate with remote server");
            }
        }

        User? contact = _usersService.Get(id);
        // If the contact doesn't exist, create it
        if (contact == null)
        {
            if (server == "localhost" || server == "localhost:54321")
            {
                return BadRequest("Contact doesn't exist");
            }

            // Create the remote contact
            contact = new User(id, name, server);
            _usersService.Add(contact);
        }

        // Add the contact to the current user's names
        currentUser.ContactsIds.Add(id);
        currentUser.ContactsNames.Add(name);

        // Create a new chat between the current user and the contact
        var newChat = new Chat(currentUser.Username + "-" + contact.Username)
        {
            Members = new List<User> { currentUser, contact }
        };
        _chatsService.Add(newChat);
        // Add the new chat to the current user
        currentUser.Chats.Add(newChat);
        _usersService.Update(currentUser);
        // Add the new chat to the contact
        if (contact.Server == "localhost")
        {
            contact.Chats.Add(newChat);
            contact.ContactsIds.Add(currentUser.Username);
            contact.ContactsNames.Add(currentUser.Name);
            _usersService.Update(contact);
        }
        if (_tokens.ContainsKey(id))
        {
            _sender.Send(_tokens[id], currentUser.Username, _getCurrentUser().Name + " has created a chat room with you!", id);
        }

        _hubContext.Clients.All.SendAsync("MessageReceived");
        return Created("", null);
    }

    [HttpGet("{id}")]
    [Authorize]
    public IActionResult Get(string id)
    {
        // Get a contact of the current user by id

        if (_usersService.Get(id) == null)
        {
            return NotFound();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        string? contactId = currentUser.ContactsIds.Find(username => username == id);
        if (contactId == null)
        {
            return NotFound();
        }

        var contact = _usersService.Get(contactId);
        if (contact == null)
        {
            return NotFound();
        }

        string? displayName = currentUser.ContactsNames[currentUser.ContactsIds.IndexOf(contact.Username)];
        var outerContact = new OuterUser(contactId, currentUser.Chats[currentUser.ContactsIds.IndexOf(contactId)], displayName);
        return Ok(outerContact);
    }

    [HttpPut("{id}")]
    [Authorize]
    public IActionResult Put(string id, [FromBody] JsonElement body)
    {
        // Update a contact of the current user by id

        string? name, server;
        try
        {
            name = body.GetProperty("name").GetString();
            server = body.GetProperty("server").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (name == null || server == null)
        {
            return BadRequest();
        }

        if (_usersService.Get(id) == null)
        {
            return NotFound();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        var contactId = currentUser.ContactsIds.Find(username => username == id);
        if (contactId == null)
        {
            return NotFound();
        }

        var newContact = _usersService.Get(id);
        if (newContact == null)
        {
            return NotFound();
        }

        currentUser.ContactsNames[currentUser.ContactsIds.IndexOf(contactId)] = name;
        newContact.Server = server;

        if (_usersService.Update(newContact) == null)
        {
            return NotFound();
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize]
    public IActionResult Delete(string id)
    {
        // Delete a contact of the current user by id

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        var contact = _usersService.Get(id);
        if (contact == null)
        {
            return NotFound();
        }

        // Delete the chat between the current user and the contact
        var chat = currentUser.Chats.Find(chat => chat.Id == contact.Chats[contact.ContactsNames.IndexOf(currentUser.Username)].Id);
        if (chat == null)
        {
            return NotFound();
        }

        _chatsService.Remove(chat);
        // Delete the contact from the current user
        currentUser.Chats.Remove(chat);
        // Delete the contact from Names dictionary
        currentUser.ContactsNames.RemoveAt(currentUser.ContactsIds.IndexOf(contact.Username));
        currentUser.ContactsIds.Remove(contact.Username);

        _usersService.Update(currentUser);
        // Delete the currentUser from the contact
        contact.Chats.Remove(chat);
        contact.ContactsNames.RemoveAt(contact.ContactsIds.IndexOf(currentUser.Username));
        contact.ContactsIds.Remove(currentUser.Username);
        _usersService.Update(contact);
        return NoContent();
    }

    [HttpGet("{id}/messages")]
    [Authorize]
    public IActionResult GetMessages(string id)
    {
        // Get all messages between the current user and the contact by id

        if (_usersService.Get(id) == null)
        {
            return NotFound();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        // If the contact is not in the current user's contacts, return NotFound
        if (!currentUser.ContactsIds.Contains(id))
        {
            return NotFound();
        }

        List<OuterMessage> outerMessages = new List<OuterMessage>();
        // Loop over currentUser.Chats[id].Messages
        int i = 0;
        foreach (var m in currentUser.Chats[currentUser.ContactsIds.IndexOf(id)].Messages)
        {
            // Create a new OuterMessage
            OuterMessage outerMessage = new OuterMessage(++i, m, currentUser.Username);
            outerMessages.Add(outerMessage);
        }

        return Ok(outerMessages);
    }

    [HttpPost("{id}/messages")]
    [Authorize]
    public IActionResult PostNewMessage(string id, [FromBody] JsonElement body)
    {
        // Add a new message between the current user and the contact by id

        string? content;
        try
        {
            content = body.GetProperty("content").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (content == null)
        {
            return BadRequest();
        }

        var contact = _usersService.Get(id);
        if (contact == null)
        {
            return NotFound();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        // If the contact is not in the current user's contacts, return NotFound
        if (!currentUser.ContactsIds.Contains(id))
        {
            return NotFound();
        }

        _hubContext.Clients.All.SendAsync("MessageReceived");
        // Add the new message to the chat
        Chat chat = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)];
        // Create a new message
        int lastMessageId = chat.Messages.Count > 0 ? chat.Messages.Max(m => m.Id) : 0;
        var message = new Message(content, currentUser.Username, DateTime.Now);
        chat.Messages.Add(message);
        _chatsService.Update(chat);
        // Add the new message to the contact if on a remote server
        if (contact.Server != "localhost")
        {
            // Send transfer request
            var transfer = new Transfer(currentUser.Username, contact.Username, content);
            var json = JsonSerializer.Serialize(transfer, _jsonSerializerOptions);
            var stringContent = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            _httpClient.PostAsync("https://" + contact.Server + "/api/transfer", stringContent).Wait();
        }

        if (_tokens.ContainsKey(id))
        {
            _sender.Send(_tokens[id], currentUser.Username, message.Text, id);
        }

        return Created("", null);
    }

    [HttpGet("{id}/messages/{id2}")]
    [Authorize]
    public IActionResult GetMessage(string id, int id2)
    {
        // Get a message with id2 between the current user and the contact by id

        if (_usersService.Get(id) == null)
        {
            return NotFound();
        }


        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        // If the contact is not in the current user's contacts, return NotFound
        if (!currentUser.ContactsIds.Contains(id))
        {
            return NotFound();
        }

        // If the message doesn't exist, return NotFound
        Message? message = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)].Messages.SingleOrDefault(msg => msg.Id == id2);
        if (message == null)
        {
            return NotFound();
        }

        OuterMessage outerMessage = new OuterMessage(id2, message, currentUser.Name);
        return Ok(outerMessage);
    }

    [HttpPut("{id}/messages/{id2}")]
    [Authorize]
    public IActionResult PutMessage(string id, int id2, [FromBody] JsonElement body)
    {
        // Update a message with id2 between the current user and the contact by id

        string? content;
        try
        {
            content = body.GetProperty("content").GetString();
        }
        catch (Exception)
        {
            return BadRequest();
        }

        if (content == null)
        {
            return BadRequest();
        }

        var contact = _usersService.Get(id);
        if (contact == null)
        {
            return NotFound();
        }

        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        // If the contact is not in the current user's contacts, return NotFound
        if (!currentUser.ContactsIds.Contains(id))
        {
            return NotFound();
        }

        // If the message doesn't exist, return NotFound
        var message = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)].Messages.SingleOrDefault(msg => msg.Id == id2);
        if (message == null)
        {
            return NotFound();
        }

        // Update the message
        message.Text = content;
        // Update the chat
        Chat chat = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)];
        _chatsService.Update(chat);
        // No API exists for updating the contact if on a remote server

        return NoContent();
    }

    [HttpDelete("{id}/messages/{id2}")]
    [Authorize]
    public IActionResult DeleteMessage(string id, int id2)
    {
        // Delete a message with id2 between the current user and the contact by id

        var contact = _usersService.Get(id);
        if (contact == null)
        {
            return NotFound();
        }


        var currentUser = _getCurrentUser();
        if (currentUser == null)
        {
            return NotFound();
        }

        if (currentUser.Username == id)
        {
            return BadRequest();
        }

        // If the contact is not in the current user's contacts, return NotFound
        if (!currentUser.ContactsIds.Contains(id))
        {
            return NotFound();
        }

        // If the message doesn't exist, return NotFound
        var message = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)].Messages.SingleOrDefault(msg => msg.Id == id2);
        if (message == null)
        {
            return NotFound();
        }

        // Delete the message from the chat
        Chat chat = currentUser.Chats[currentUser.ContactsIds.IndexOf(id)];
        chat.Messages.Remove(message);
        _chatsService.Update(chat);
        // No API exists for deleting a message from the contact if on a remote server
        return NoContent();
    }
}