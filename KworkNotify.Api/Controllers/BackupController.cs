﻿using CliWrap;
using CliWrap.Buffered;
using KworkNotify.Core.Service;
using KworkNotify.Core.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace KworkNotify.Api.Controllers;

[Authorize]
[Route("backup")]
[ApiController]
public class BackupController(TelegramData data, IOptions<AppSettings> settings) : ControllerBase
{
    [HttpGet("create")]
    public async Task<ActionResult> CreateBackup()
    {
        try
        {
            var result = await Cli.Wrap("bash")
                .WithArguments(settings.Value.BackupScriptPath)
                .WithWorkingDirectory("/root")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
        
            var stdout = result.StandardOutput;
            var stderr = result.StandardError;
        
            if (!string.IsNullOrEmpty(stderr) || result.ExitCode != 0)
            {
                return BadRequest(new { Output = stdout, Error = stderr, ExitCode = result.ExitCode });
            }
        
            return Ok(new { Output = stdout });
        }
        catch (Exception e)
        {
            const string error = "failed to create backup";
            Log.ForContext<BackupController>().Error(e, error);
            return StatusCode(500, error);
        }
    }
    [HttpGet("send")]
    public async Task<ActionResult> SendBackup()
    {
        try
        {
            if (data.Bot is not { } bot) return StatusCode(500, "Bot is not initialized");
            if (settings.Value.AdminIds.Count <= 0) return StatusCode(500, "Admin Ids are required");
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
            var time = DateTime.UtcNow + timeZone.BaseUtcOffset;
            var backupFiles = Directory.EnumerateFiles("/root", "*.gz").ToList();
            foreach (var backupFile in backupFiles)
            {
                try
                {
                    await using var stream = new FileStream(backupFile, FileMode.Open);
                    var input = new InputFileStream(stream, backupFile);
                    var caption = time.ToString("HH:mm:ss");
                    await bot.Client.TelegramClient.SendDocumentAsync(new ChatId(settings.Value.AdminIds.First()), document: input, caption: caption);
                }
                catch (Exception e)
                {
                    Log.ForContext<BackupController>().Error(e, "failed to read backup file: {BackupFile}", backupFile);
                }
            }
            
            return Ok();
        }
        catch (Exception e)
        {
            const string error = "failed to send backup";
            Log.ForContext<BackupController>().Error(e, error);
            return StatusCode(500, error);
        }
    }
}