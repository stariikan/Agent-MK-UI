@echo off
TITLE Local Agent Studio

echo ===================================================
echo     Starting Local Agentic Pipeline (LangGraph)
echo ===================================================

:: 1. Check if Ollama is running
ollama list >nul 2>&1
IF %ERRORLEVEL% NEQ 0 (
    echo [WARNING] Ollama is not running or not installed!
    echo Please start the Ollama application from your Windows Start Menu.
    pause
    exit /b
)

:: 2. Check if virtual environment exists; create and install if missing
IF NOT EXIST "agent-env\Scripts\activate.bat" (
    echo [INFO] First-time run detected. Creating Python virtual environment...
    python -m venv agent-env
    
    echo [INFO] Activating virtual environment...
    call .\agent-env\Scripts\activate.bat
    
    echo [INFO] Installing required dependencies (this may take a minute)...
    pip install -U langgraph langgraph-cli langchain-ollama langchain-core pydantic rich
    echo [INFO] Dependencies installed!
) ELSE (
    echo [INFO] Activating existing virtual environment...
    call .\agent-env\Scripts\activate.bat
)

:: 3. Verify langgraph.json exists
IF NOT EXIST "langgraph.json" (
    echo [ERROR] langgraph.json not found! 
    echo Please ensure langgraph.json and agent.py are in this folder.
    pause
    exit /b
)

echo ===================================================
echo [INFO] Starting LangGraph Studio Web Server...
echo [INFO] Your browser should open automatically.
echo ===================================================

:: 4. Start the Studio
langgraph dev

pause