# Agentic Orchestra

Agentic Orchestra is a robust, cross-platform CLI AI Orchestrator built with C# and .NET 8. It provides an interactive terminal interface to converse with Large Language Models.

The tool features a **hybrid pipeline**:
1. It attempts to connect to a local **Ollama** instance first for fast, private, and local execution.
2. If Ollama is unavailable, it automatically falls back to web-based AI platforms (like **Google Gemini**) using **Playwright** browser automation.

## Features

- **Interactive UI**: Built with `Spectre.Console` for a visually rich experience including spinners, formatted tables, and intuitive menus.
- **Cross-Platform**: Designed natively for both Windows and Linux. Zero hardcoded paths; uses standard OS-specific data folders (`~/.config/AgenticOrchestra` or `%APPDATA%\AgenticOrchestra`).
- **Hybrid AI Pipeline**:
  - **Local First**: Prioritizes `llama3.2` via `http://localhost:11434` (Ollama REST API).
  - **Web Fallback**: Automatically creates a headless/headed Chromium instance to interact with Google Gemini if the local API is unreachable.
- **Persistent Sessions**: Playwright contexts are saved to the user's local application data folder, meaning you only need to log in to Gemini once!

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- (Optional) [Ollama](https://ollama.com/) running locally with models pulled (`ollama pull llama3.2`).

## Installation & Setup

1. Clone or download the repository.
2. Build the project:
   ```bash
   dotnet build src/AgenticOrchestra/AgenticOrchestra.csproj
   ```
3. Run the application:
   ```bash
   dotnet run --project src/AgenticOrchestra/AgenticOrchestra.csproj
   ```

*Note: On your very first run, the tool will automatically download the required Playwright Chromium binaries.*

## Configuration

Settings are managed via a `config.json` file created automatically on first run.

- **Windows**: `C:\Users\<Name>\AppData\Roaming\AgenticOrchestra\config.json`
- **Linux**: `~/.config/AgenticOrchestra/config.json`

You can edit this file directly or use the **Settings** menu within the CLI.

## How the Web Fallback Works

If Ollama fails to respond, the app spins up a dedicated Playwright browser profile.
- On your first web interaction, if you are not logged into Google, it will fail to find the prompt box.
- Set `WebFallback: Headless` to `false` in the settings.
- Run the tool. The browser will open visually. Log into Google/Gemini manually.
- Close the app and re-enable `Headless`. Your session is now preserved for all future automated interactions!