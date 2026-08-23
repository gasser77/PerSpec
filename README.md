# PerSpec: Command-Line Superpowers for Unity

<div align="center">

```
8888888b.                  .d8888b.                             
888   Y88b                d88P  Y88b                            
888    888                Y88b.                                 
888   d88P .d88b.  888d888 "Y888b.   88888b.   .d88b.   .d8888b 
8888888P" d8P  Y8b 888P"      "Y88b. 888 "88b d8P  Y8b d88P"    
888       88888888 888          "888 888  888 88888888 888      
888       Y8b.     888    Y88b  d88P 888 d88P Y8b.     Y88b.    
888        "Y8888  888     "Y8888P"  88888P"   "Y8888   "Y8888P 
                                     888                        
                                     888                        
                                     
```

**Tired of copy-pasting Unity errors into your LLM? Frustrated with the endless cycle of switching back to the editor just to click "Run"?**

PerSpec bridges the gap between your terminal and the Unity Editor, giving you powerful command-line control over your development workflow. Stop the context switching. Start building faster.

</div>

## What is PerSpec?

PerSpec is a lightweight, rock-solid toolkit that connects your terminal to the Unity Editor using a simple but powerful combination of Python scripts and a local SQLite database. It allows you to **refresh assets, check for errors, run tests, and view logs** without ever leaving your command line.

It's designed for modern, test-driven development and is perfect for solo developers, teams, and even for coordinating with AI coding assistants.

## Quick Start

1. **Install via OpenUPM (Recommended):**
    * Install the OpenUPM CLI if you haven't already:
      ```bash
      npm install -g openupm-cli
      ```
    * Add PerSpec to your project:
      ```bash
      openupm add com.digitraver.perspec
      ```
    * Or visit [OpenUPM](https://openupm.com/packages/com.digitraver.perspec/) for alternative installation methods.

    <details>
    <summary><b>Manual Installation (Alternative)</b></summary>

    If you prefer to manually edit your `Packages/manifest.json`, add the following. **All three scopes are required** for dependency resolution:

    ```json
    "scopedRegistries": [
      {
        "name": "package.openupm.com",
        "url": "https://package.openupm.com",
        "scopes": [
          "com.digitraver.perspec",
          "com.cysharp.unitask",
          "com.gilzoide.sqlite-net"
        ]
      }
    ],
    "dependencies": {
      "com.digitraver.perspec": "1.8.1"
    }
    ```

    > **Note:** Use an exact version. Unity's project manifest does not support npm-style version ranges such as `^1.8.0`, unlike npm or a package's own `package.json`. To upgrade, change the number and let Unity re-resolve.

    > **Why all three scopes?** PerSpec depends on `com.cysharp.unitask` and `com.gilzoide.sqlite-net`, which are also OpenUPM packages. Omit either scope and Unity queries the default Unity registry instead, which does not host them, producing `Package [com.gilzoide.sqlite-net@1.3.1] cannot be found`. If that happens, run `Tools > PerSpec > Repair Package Dependencies`.

    </details>

2. **Initialize PerSpec in Unity:**
    * Once the package is installed, go to `Tools > PerSpec > Initialize PerSpec`.
    * This creates the `PerSpec/` directory in your project root, which contains the coordination database and Python scripts.

3. **Open the Control Center:**
    * Go to `Tools > PerSpec > Control Center` to access all PerSpec features through a user-friendly interface.

You're all set! You can now control Unity from the Control Center.

## Command Reference

Here are the key actions available through the **Control Center** (`Tools > PerSpec > Control Center`):

### Unity Interaction

| Action | Description | Control Center Location |
|--------|-------------|------------------------|
| **Refresh Assets** | Triggers Unity to re-import and compile your code | Debug Settings > Force Compilation |
| **Save Project** | Saves the current scene and project changes | Use Unity's File menu directly |
| **Open Console** | Opens the Unity Console window | Use Unity's Window menu directly |

### Viewing Logs

| Action | Description | Control Center Location |
|--------|-------------|------------------------|
| **View Compilation Errors** | Displays C# compilation errors from the editor | Test Coordinator > Test Status |
| **View Recent Logs** | Shows recent log entries from the current session | Test Coordinator > Status Display |
| **Database Statistics** | Shows database size and maintenance status | Test Coordinator > Database Status |

### Running Tests & Viewing Results

| Action | Description | Control Center Location |
|--------|-------------|------------------------|
| **Run EditMode Tests** | Executes all EditMode tests in your project | Test Coordinator (auto-polling enabled) |
| **Run PlayMode Tests** | Executes all PlayMode tests in your project | Test Coordinator (auto-polling enabled) |
| **View Test Results** | Shows a summary of the latest test run | Test Coordinator > Test Status |
| **Cancel Running Test** | Cancels the currently executing test | Test Coordinator > Cancel Current Test |

### Maintenance

| Action | Description | Control Center Location |
|--------|-------------|------------------------|
| **Quick Cleanup** | Clears old logs and compacts the database | Test Coordinator > Clean Old Data (2h) |
| **Vacuum Database** | Optimizes database performance | Test Coordinator > Vacuum Database |
| **Aggressive Cleanup** | Deep clean for databases over 10MB | Test Coordinator > Aggressive Cleanup |
| **Reset Database** | Complete database reset | Test Coordinator > Reset Database |

### Advanced Features (Command Line)

For advanced users and AI/LLM integration, PerSpec also provides Python scripts for programmatic control. See the [AI/LLM Integration Guide](Documentation/LLM.md) for details on command-line usage.

## The Recommended TDD Workflow

PerSpec is built for a fast, iterative Test-Driven Development (TDD) workflow. Follow this cycle for maximum efficiency and confidence.

> **Note:** When working with AI assistants like Claude Code, steps 2-4 can be automated with natural language prompts. See [Automating with LLM Prompts](#automating-with-llm-prompts) below.

#### **Step 1: Write Your Code & Tests**
Create your feature and a corresponding test file in your IDE.

#### **Step 2: Refresh Unity**
Tell Unity to import the new files and get ready for compilation.
* **Manual:** Open **Control Center > Debug Settings** and click **Force Compilation**
* **LLM Prompt:** Say **"refresh Unity"**

#### **Step 3: Check for Compilation Errors**
Before you run any tests, make sure your code actually compiles!
* **Manual:** Check **Control Center > Test Coordinator** for the current status
* **LLM Prompt:** Say **"show errors"** or **"get errors"**
* Any C# errors will be displayed in the Test Status section

> **Pro Tip:** If you see errors, fix them and go back to Step 2. Don't run tests if your code doesn't compile!

#### **Step 4: Run Your Tests**
Execute your test suite and see the results.
* **Manual:** The **Test Coordinator** with auto-polling enabled will automatically pick up and run pending tests
* **LLM Prompt:** Say **"run tests"**
* View results in the **Test Status** section

Rinse and repeat! This simple loop ensures you're always working with a stable, tested codebase.

### Automating with LLM Prompts

When working with an LLM assistant (like Claude Code), you can automate the TDD workflow with natural language commands:

| Step | Manual Action | LLM Prompt | What It Does |
|------|---------------|------------|--------------|
| **2** | Control Center > Force Compilation | `"refresh Unity"` | Runs `quick_refresh.py full --wait` to reimport assets and compile |
| **3** | Control Center > Test Status | `"show errors"` or `"get errors"` | Runs `monitor_editmode_logs.py --errors` to display compilation errors |
| **4** | Control Center > Test Coordinator | `"run tests"` | Runs `quick_test.py all -p edit --wait` to execute test suite |

**Automatic Workflow:** Capable LLMs will automatically run steps 2-3 after writing or modifying any C# code to verify compilation before considering the task complete.

For complete command-line documentation and advanced usage, see the [AI/LLM Integration Guide](Documentation/LLM.md).

## For Power Users & AI Integration

PerSpec was originally built to help guide LLM assistants like Claude and Gemini. The robust command-line interface is perfect for programmatic use. If you want to dive deeper into the architecture or integrate PerSpec with an AI, check out the detailed documentation:

*   **[AI/LLM Integration Guide](Documentation/LLM.md)**

## Contributing

PerSpec is open source! Contributions are welcome. Fork the repository and help us make Unity development faster and more reliable for everyone.

*   **Repo:** [https://github.com/thePostFuturist/PerSpec](https://github.com/thePostFuturist/PerSpec)

## License

MIT License - Use it, modify it, ship it!
