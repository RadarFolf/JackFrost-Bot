﻿using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClutteredMarkov;

namespace JackFrostBot
{
    public class Processing
    {
        //Log each message and url that comes in
        public static void LogSentMessage(SocketMessage message)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            string logline = ($"<{DateTime.Now.ToString("hh:mm")}> {user.Username} in #{message.Channel}: {message}");
            if (message.Attachments.Count > 0)
                logline = logline + message.Attachments.FirstOrDefault().Url;
            Console.WriteLine(logline);
            File.AppendAllText($"{guild.Id}\\Log.txt", logline + "\n");
        }

        //Write info to both the console and the log txt file
        public static void LogConsoleText(string text, ulong guildId)
        {
            string logline = ($"<{DateTime.Now.ToString("hh:mm")}>: {text}");
            Console.WriteLine(logline);
            File.AppendAllText($"{guildId.ToString()}\\Log.txt", logline + "\n");
        }

        //Write info on the last deleted message to a txt file
        public static async Task LogDeletedMessage(SocketMessage message, string reason)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            LogConsoleText(reason, guild.Id);
            await message.DeleteAsync();
            File.WriteAllText($"{guild.Id.ToString()}\\LastDeletedMsg.txt", $"**Author**: {user.Username} ({user.Id})\n**Time**: {message.Timestamp}\n**Reason**: {reason}");
        }

        //Prevent user attempting to talk in #voice-text while not in a voice channel
        public static async Task VoiceTextCheck(SocketMessage message, SocketGuildChannel channel)
        {
            if (channel.Id == Setup.VoiceTextChannelId(channel.Guild.Id) && !message.Author.IsBot && !Moderation.IsModerator((IGuildUser)message.Author))
            {
                var author = (SocketGuildUser)message.Author;
                try { var vc = author.VoiceChannel.Id; }
                catch
                {
                    await LogDeletedMessage(message, "User tried to talk in the voice-text chat while not in a voice channel.");
                }
            }
        }

        //Grant a user a role if they type the password in the verification channel
        public static async Task NewArrivalsCheck(SocketMessage message)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            if (message.Content.ToLower() == Setup.PasswordString(guild.Id).ToLower() && message.Channel.Id == Setup.VerificationChannelId(guild.Id))
            {
                await message.DeleteAsync();
                ulong memberRoleId = Setup.MemberRoleId(guild.Id);
                if (memberRoleId != 0)
                {
                    var memberRole = user.Guild.GetRole(memberRoleId);
                    await user.AddRoleAsync(memberRole);
                    var embed = Embeds.LogMemberAdd(user);
                    var user2 = (SocketGuildUser)user;
                    var botlog = (ITextChannel)user2.Guild.GetChannel(Setup.BogLotChannelId(guild.Id));
                    await botlog.SendMessageAsync("", embed: embed).ConfigureAwait(false);
                }
                else
                {
                    await message.Channel.SendMessageAsync("Attempted to give the new member a role, but it has to be saved in the setup.ini first!");
                }
                
            }
            else if (message.Channel.Id == Setup.VerificationChannelId(guild.Id) && (message.MentionedUsers.Count == 0 || message.MentionedRoles.Count == 0))
            {
                //Delete the message if it doesn't match h, unless it's a mention
                await message.DeleteAsync();
            }
        }

        //Delete text posts in memes chat
        public static async Task MemesCheck(SocketMessage message)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            if (message.Attachments.Count == 0 && message.Channel.Id == Setup.MediaChannelId(guild.Id) && !message.Content.Contains("http") && !message.Content.Contains("www"))
            {
                await LogDeletedMessage(message, "User tried to talk in the memes channel.");
            }
        }

        // Check if a message is a duplicate and if so, delete it
        public static async Task DuplicateMsgCheck(SocketMessage message, SocketGuildChannel channel)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            if ((channel.Id != Setup.BotSandBoxChannelId(guild.Id)) && (Moderation.IsModerator((IGuildUser)message.Author) == false) && (message.Author.IsBot == false))
            {
                int runs = 0;
                int matches = 0;
                foreach (IMessage msg in (await message.Channel.GetMessagesAsync(10).FlattenAsync()))
                {
                    if (((runs > 0) && (msg.Content.ToString() == message.Content.ToString()) && (msg.Author == message.Author) && (message.Attachments.Count == 0)))
                    {
                        matches++;
                        if (matches >= Setup.DuplicateMsgLimit(channel.Guild.Id))
                        {
                            await LogDeletedMessage(message, "Duplicate message");
                            Moderation.Warn(Setup.BotName(channel.Guild.Id), (ITextChannel)channel, (SocketGuildUser)message.Author, "Stop posting the same thing over and over.");
                        }
                    }
                    runs++;
                }
                //Delete Messages that are too short
                int minimum = Setup.MsgLengthLimit(channel.Guild.Id);
                int msgLength = message.Content.ToString().Trim(new char[] { ' ', '.' }).Length;
                if (message.Content.ToString().Length < minimum && message.Attachments.Count == 0)
                {
                    await LogDeletedMessage(message, $"Message was too short (minimum is {minimum})");
                }
            }
        }

        //Check if a message is filtered 
        public static bool FilterCheck(SocketMessage message, SocketGuildChannel channel, out string deleteReason)
        {
            bool filtered = false;
            deleteReason = "";

            if (Setup.EnableWordFilter(channel.Guild.Id))
            {
                if (!File.Exists($"{channel.Guild.Id.ToString()}\\filter.txt"))
                {
                    File.CreateText($"{channel.Guild.Id.ToString()}\\filter.txt").Close();
                }
                if (!File.Exists($"{channel.Guild.Id.ToString()}\\filterbypasscheck.txt"))
                {
                    File.CreateText($"{channel.Guild.Id.ToString()}\\filterbypasscheck.txt").Close();
                }

                foreach (string term in File.ReadLines($"{channel.Guild.Id.ToString()}\\filter.txt"))
                {
                    var msgText = message.Content.ToLower();
                    if (Regex.IsMatch(msgText, string.Format(@"\b{0}\b|\b{0}d\b|\b{0}s\b|\b{0}ing\b|\b{0}ed\b|\b{0}er\b|\b{0}ers\b", Regex.Escape(term))))
                    {
                        //if so then delete it
                        deleteReason = $"Message included a banned term: {term}\nDiscussion involving this term is not prohibited.";
                        filtered = true;

                        //Check if a message is clearly bypassing the filter, if so then warn
                        foreach (string bypassTerm in File.ReadLines($"{channel.Guild.Id.ToString()}\\filterbypasscheck.txt"))
                        {
                            if (term == bypassTerm)
                            {
                                deleteReason = $"Message included matched a banned term: {term}\nA clear attempt was made to bypass the word filter.";
                                Moderation.Warn(Setup.BotName(channel.Guild.Id), (ITextChannel)channel, (SocketGuildUser)message.Author, "Stop trying to bypass the word filter.");
                                filtered = true;
                            }
                        }
                    }
                }
            }

            return filtered;
        }

        //Log replies and respond with a random reply
        public static async Task Markov(string message, SocketGuildChannel channel, int frequency)
        {
            string binFilePath = $"{Directory.GetCurrentDirectory()}\\{channel.Guild.Id.ToString()}\\{channel.Guild.Id.ToString()}";
            Markov mkv = new Markov();
            try { mkv.LoadChainState(binFilePath); }
            catch { }
            //Sanitize message content
            string newMessage = message;
            var links = newMessage.Split("\t\n ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries).Where(s => s.StartsWith("http://") || s.StartsWith("www.") || s.StartsWith("https://") || s.StartsWith("@"));
            foreach (var link in links)
            {
                newMessage = newMessage.Replace(link, "");
            }
            if (newMessage != "")
                mkv.Feed(newMessage);

            mkv.SaveChainState(binFilePath);
            if (channel.Id == Setup.BotSandBoxChannelId(channel.Guild.Id))
            {
                var socketChannel = (ISocketMessageChannel)channel;
                Random rnd = new Random();
                if (rnd.Next(1, 100) <= frequency)
                    await socketChannel.SendMessageAsync(MarkovGenerator.Create(mkv));
                else
                {
                    await socketChannel.SendMessageAsync("");
                }
            }
        }

        //Check if a user levelled up
        public static async Task LevelUpCheck(SocketMessage message)
        {
            var user = (IGuildUser)message.Author;
            var guild = user.Guild;
            string levelTxtPath = $"{guild.Id.ToString()}\\levelsystem.ini";
            if (!File.Exists(levelTxtPath))
                using (StreamWriter sw = File.CreateText(levelTxtPath))
                    sw.WriteLine("[Posts]\n");

            if (Setup.EnableLevelup(guild.Id) && !message.Author.IsBot)
            {
                int postNo = Setup.GetPostNumber(guild.Id, user.Id.ToString());
                for (int i = 0; i < Setup.Levels(guild.Id).Count(); i++)
                {
                    if (postNo + 1 == Setup.Levels(guild.Id)[i])
                    {
                        //Announce the levelup
                        var embed = Embeds.LevelUp((IUser)message.Author, i + 1, Setup.Levels(guild.Id).Last(), Setup.Levels(guild.Id)[i], Setup.Levels(guild.Id)[i + 1]);
                        await message.Channel.SendMessageAsync("", embed: embed).ConfigureAwait(false);
                        //Award the user a new role if one is specified for the level
                        var newRole = guild.GetRole(Setup.LevelUpRoleId(user.Guild.Id, i + 1));
                        if (newRole != null)
                            await user.AddRoleAsync(newRole);
                    }
                }
                postNo = postNo + 1;
                Setup.UpdatePostNumber(guild.Id, message.Author.Id.ToString(), postNo.ToString());
            }
        }
    }
}
