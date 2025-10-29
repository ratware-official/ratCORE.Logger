## ratCORE.Logger

**ratCORE.Logger** is a C# library providing a **thread-safe, asynchronous logging system** for console and file output.  
It is lightweight, highly configurable, and designed to never block or crash the hosting application.

---

### 🚀 Features

- **Thread-safe asynchronous design**  
  Uses a background worker thread and a bounded queue to handle logs efficiently without blocking.

- **Configurable log levels**  
  Supports `Debug`, `Info`, `Warning`, `Error`, `Critical`, and `None`.

- **Console and file output**  
  Choose between console, file, or both. Logs are flushed in configurable batches.

- **Automatic file rolling**  
  When the file exceeds the configured size limit, a new timestamped log file is created automatically.

- **Structured JSON or human-readable text**  
  Switch between plain text logs and structured JSON output for machine processing.

- **Prefix alignment & multiline support**  
  Each log line includes a detailed prefix (timestamp, level, app, thread, scope, source) and handles multi-line messages gracefully.

- **Safe shutdown & flushing**  
  Ensures all queued entries are written before disposal.

---

### 🧩 Example Usage / Quick Start

```csharp
using ratCORE.Logger;

var logger = new Logger(new LoggerConfig
{
    ApplicationName = "ratCORE.DemoApp",
    MinimumLevel = LogLevel.Debug,
    WriteToConsole = true,
    WriteToFile = true,
    StructuredJson = false,            // set true for JSON logs
    LogDirectory = "logs",
    LogFileName = "demo.log",
    MaxFileBytes = 5 * 1024 * 1024     // 5 MB file rolling
});

logger.Info("Application started");
logger.Debug("Detailed debug output", scope: "Init");

try
{
    throw new InvalidOperationException("Simulated failure");
}
catch (Exception ex)
{
    logger.Error("An error occurred during startup", ex, scope: "Init");
}

// Ensure all logs are written before exit
logger.Flush();
logger.Dispose();
```

---

### 🧱 Configuration Options (`LoggerConfig`)

| Property | Description | Default |
|-----------|--------------|----------|
| `MinimumLevel` | Minimum log level to process (`None` disables all logging). | `Info` |
| `WriteToConsole` | Write logs to console output. | `false` |
| `WriteToFile` | Write logs to a file. | `true` |
| `LogDirectory` | Directory for log files. | `"logs"` |
| `LogFileName` | Base name of the log file. | `"app.log"` |
| `MaxFileBytes` | Maximum file size before rolling. | `5 MB` |
| `UseUtcTimestamps` | Use UTC instead of local time. | `true` |
| `StructuredJson` | Output logs as JSON instead of text. | `false` |
| `ApplicationName` | Optional name included in every log entry. | `null` |
| `BackgroundBatchSize` | Number of log entries per flush. | `128` |
| `FlushInterval` | Max wait time before forced flush. | `400 ms` |
| `QueueBoundedCapacity` | Maximum entries in queue (prevents OOM). | `10 000` |

---

### 🧪 Log Format (Text Mode)

```
DD.MM.YYYY HH:mm:ss,ffff | LEVEL    | App | ThreadID | Scope | SourceFile:Line Member() | Message
```

**Example:**
```
29.10.2025 20:41:02,4555 | DEBUG    | ratCORE.Demo | 8 | Init | Program.cs:42 Main() | Starting initialization
29.10.2025 20:41:02,4556 | ERROR    | ratCORE.Demo | 8 | Init | Program.cs:42 Main() | Simulated failure
```

---

### ⚙️ Internals Overview

| Component | Purpose |
|------------|----------|
| **BlockingCollection<LogEntry>** | Thread-safe queue for pending log entries. |
| **Worker Thread** | Processes queued logs asynchronously and writes them to output. |
| **FlushBuffer()** | Handles batch writing to file and/or console. |
| **RollIfNeeded()** | Performs file rotation when size exceeds limit. |
| **FormatText() / FormatJson()** | Converts entries into readable or structured formats. |
| **Dispose()** | Gracefully stops the worker and flushes remaining entries. |

---

### 🧠 Design Philosophy

`ratCORE.Logger` is designed for **robust, non-intrusive logging** in both development and production environments.  
It guarantees that logging operations never throw exceptions back to the caller and have minimal runtime overhead.

---

### 🛠️ System Requirements

- .NET 8 or higher  
- Works on **Windows**, **Linux**, and **macOS**  
- No external dependencies  

---

### 🧩 About

Part of the **ratCORE** framework — a modular collection of C# libraries for secure, performant, and cross-platform development.

---

**License:** Creative Commons Attribution 4.0 International (CC BY 4.0)  
**Copyright © 2025 ratware**
