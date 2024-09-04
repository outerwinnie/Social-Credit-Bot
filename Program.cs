﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using Discord;
using Discord.WebSocket;
using Discord.Interactions;

class Program
{
    private static async Task Main(string[] args)
    {
        var bot = new Bot();
        await bot.StartAsync();
        await Task.Delay(-1); // Prevents the application from exiting
    }
}

class Bot
{
    private readonly DiscordSocketClient _client = new DiscordSocketClient();
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _services;
    private readonly string _csvFilePath;
    private readonly string _ignoredUsersCsvFilePath;
    private readonly HashSet<ulong> _ignoredUsers = new HashSet<ulong>(); // Collection for ignored users
    private readonly Dictionary<ulong, int> _userReactionCounts = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, HashSet<ulong>> _userMessageReactions = new Dictionary<ulong, HashSet<ulong>>(); // Dictionary to track reactions
    private readonly int _reactionIncrement;
    private readonly ulong _guildId; // Single guild ID for command registration

    public Bot()
    {
        _csvFilePath = Environment.GetEnvironmentVariable("CSV_FILE_PATH") ?? "user_reactions.csv";
        _ignoredUsersCsvFilePath = Environment.GetEnvironmentVariable("IGNORED_USERS_CSV_PATH") ?? "ignored_users.csv";
        _interactionService = new InteractionService(_client.Rest);
        _guildId = GetGuildId(); // Get guild ID from environment variable

        if (!int.TryParse(Environment.GetEnvironmentVariable("REACTION_INCREMENT"), out _reactionIncrement))
        {
            _reactionIncrement = 1; // Default value if the environment variable is not set or invalid
        }
    }

    public async Task StartAsync()
    {
        _client.Log += LogAsync;
        _client.ReactionAdded += ReactionAddedAsync;
        _client.Ready += ReadyAsync;

        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");
        }

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Register slash commands
        _client.SlashCommandExecuted += HandleSlashCommandAsync;

        // Load existing data from the CSV files
        LoadData();
        LoadIgnoredUsers();

        Console.WriteLine("Bot is running...");
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }

    private async Task ReadyAsync()
    {
        // Register the commands when the bot is ready
        await RegisterCommandsAsync();
    }

    private async Task RegisterCommandsAsync()
    {
        // Create the /ignorar command
        var commandBuilder = new SlashCommandBuilder()
            .WithName("ignorar")
            .WithDescription("Opt-out from participating in reaction tracking.");

        // Register commands for the specified guild
        if (_guildId != 0)
        {
            await _client.Rest.CreateGuildCommand(commandBuilder.Build(), _guildId);
            Console.WriteLine($"Slash command registered for guild {_guildId}.");
        }
        else
        {
            Console.WriteLine("Guild ID is not set. Slash command not registered.");
        }
    }

    private async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.CommandName == "ignorar")
        {
            var userId = command.User.Id;

            // Add the user to the ignored list
            AddIgnoredUser(userId);
            await command.RespondAsync($"{command.User.Username}, you have been added to the ignored users list. You will no longer participate in reaction tracking.");
        }
    }

    private ulong GetGuildId()
    {
        var guildIdString = Environment.GetEnvironmentVariable("GUILD_ID");
        if (ulong.TryParse(guildIdString, out var guildId))
        {
            return guildId;
        }
        return 0; // Default to 0 if the environment variable is not set or invalid
    }

    private async Task ReactionAddedAsync(Cacheable<IUserMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction)
    {
        var message = await cacheable.GetOrDownloadAsync();
        var messageId = message.Id;
        var messageAuthorId = message.Author.Id;
        var userId = reaction.UserId;

        // Ignore reactions from the message author themselves or from ignored users
        if (userId == messageAuthorId || _ignoredUsers.Contains(userId))
        {
            return; // Do nothing if the reaction is from the message author or an ignored user
        }

        // Ensure the reaction tracking dictionary is initialized
        if (!_userMessageReactions.ContainsKey(messageAuthorId))
        {
            _userMessageReactions[messageAuthorId] = new HashSet<ulong>();
        }

        lock (_userMessageReactions)
        {
            if (!_userMessageReactions[messageAuthorId].Contains(messageId))
            {
                // New reaction from this user to this message
                _userMessageReactions[messageAuthorId].Add(messageId);

                // Update reaction count for the message author
                if (_userReactionCounts.ContainsKey(messageAuthorId))
                {
                    _userReactionCounts[messageAuthorId] += _reactionIncrement;
                }
                else
                {
                    _userReactionCounts[messageAuthorId] = _reactionIncrement;
                }

                // Log the reaction
                var author = _client.GetUser(messageAuthorId) as SocketUser;
                var authorName = author?.Username ?? "Unknown"; // Fallback if user data is not available
                Console.WriteLine($"Message author {authorName} received a reaction. Total reactions for this user: {_userReactionCounts[messageAuthorId]}.");

                // Save data after updating the reaction count
                SaveData();
            }
        }
    }

    private void LoadData()
    {
        try
        {
            if (File.Exists(_csvFilePath))
            {
                using var reader = new StreamReader(_csvFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<ReactionLogMap>();
                var records = csv.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    _userReactionCounts[record.UserID] = record.ReactionsReceived;
                }
                Console.WriteLine("Data loaded from CSV.");
            }
            else
            {
                // If the CSV file does not exist, create it with headers
                using var writer = new StreamWriter(_csvFilePath);
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csv.WriteField("User ID");
                csv.WriteField("User Name");
                csv.WriteField("Reactions Received");
                csv.NextRecord();
                Console.WriteLine("New CSV file created with headers.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading data: {ex.Message}");
        }
    }

    private void SaveData()
    {
        try
        {
            // Read existing data into a dictionary
            var existingData = new Dictionary<ulong, ReactionLog>();
            if (File.Exists(_csvFilePath))
            {
                using var reader = new StreamReader(_csvFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<ReactionLogMap>();
                var records = csv.GetRecords<ReactionLog>();
                foreach (var record in records)
                {
                    existingData[record.UserID] = record;
                }
            }

            // Update or add reaction counts
            lock (_userReactionCounts)
            {
                foreach (var kvp in _userReactionCounts)
                {
                    if (existingData.ContainsKey(kvp.Key))
                    {
                        // Update existing record
                        existingData[kvp.Key].ReactionsReceived = kvp.Value;
                    }
                    else
                    {
                        // Add new record
                        var user = _client.GetUser(kvp.Key) as SocketUser;
                        var userName = user?.Username ?? "Unknown"; // Fallback if user data is not available
                        existingData[kvp.Key] = new ReactionLog
                        {
                            UserID = kvp.Key,
                            UserName = userName,
                            ReactionsReceived = kvp.Value
                        };
                    }
                }
            }

            // Overwrite the CSV file with updated data
            using var writer = new StreamWriter(_csvFilePath);
            using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));
            csvWriter.Context.RegisterClassMap<ReactionLogMap>();
            csvWriter.WriteRecords(existingData.Values);

            Console.WriteLine("Data saved to CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving data: {ex.Message}");
        }
    }

    private void LoadIgnoredUsers()
    {
        try
        {
            if (File.Exists(_ignoredUsersCsvFilePath))
            {
                using var reader = new StreamReader(_ignoredUsersCsvFilePath);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HeaderValidated = null, // Disable header validation
                    MissingFieldFound = null // Disable missing field validation
                });
                csv.Context.RegisterClassMap<IgnoredUserMap>();
                var records = csv.GetRecords<IgnoredUser>();
                foreach (var record in records)
                {
                    _ignoredUsers.Add(record.UserID);
                }
                Console.WriteLine("Ignored users loaded from CSV.");
            }
            else
            {
                // If the CSV file does not exist, create it with headers
                using var writer = new StreamWriter(_ignoredUsersCsvFilePath);
                using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
                csv.WriteField("User ID");
                csv.NextRecord();
                Console.WriteLine("New ignored users CSV file created with headers.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading ignored users: {ex.Message}");
        }
    }

    public void AddIgnoredUser(ulong userId)
    {
        if (_ignoredUsers.Add(userId))
        {
            SaveIgnoredUsers();
            Console.WriteLine($"User {userId} added to ignored users.");
        }
        else
        {
            Console.WriteLine($"User {userId} is already in the ignored users list.");
        }
    }

    public void RemoveIgnoredUser(ulong userId)
    {
        if (_ignoredUsers.Remove(userId))
        {
            SaveIgnoredUsers();
            Console.WriteLine($"User {userId} removed from ignored users.");
        }
        else
        {
            Console.WriteLine($"User {userId} is not in the ignored users list.");
        }
    }

    private void SaveIgnoredUsers()
    {
        try
        {
            using var writer = new StreamWriter(_ignoredUsersCsvFilePath);
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });
            csv.WriteField("User ID");
            csv.NextRecord();
            foreach (var userId in _ignoredUsers)
            {
                csv.WriteField(userId);
                csv.NextRecord();
            }
            Console.WriteLine("Ignored users saved to CSV.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving ignored users: {ex.Message}");
        }
    }
}

// Define a class to represent the CSV record for ignored users
public class IgnoredUser
{
    public ulong UserID { get; set; }
}

// Define a mapping class to map properties to CSV headers for ignored users
public sealed class IgnoredUserMap : ClassMap<IgnoredUser>
{
    public IgnoredUserMap()
    {
        Map(m => m.UserID).Name("User ID");
    }
}

// Define a class to represent the CSV record for reactions
public class ReactionLog
{
    public ulong UserID { get; set; }
    public string UserName { get; set; }
    public int ReactionsReceived { get; set; }
}

// Define a mapping class to map properties to CSV headers for reactions
public sealed class ReactionLogMap : ClassMap<ReactionLog>
{
    public ReactionLogMap()
    {
        Map(m => m.UserID).Name("User ID");
        Map(m => m.UserName).Name("User Name");
        Map(m => m.ReactionsReceived).Name("Reactions Received");
    }
}
