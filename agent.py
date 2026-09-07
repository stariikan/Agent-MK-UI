import sys
import os
import json
import time
import subprocess
import re
import ast
import operator
import logging
import difflib
from typing import TypedDict, Optional, Annotated
from langchain_ollama import ChatOllama
from langchain_core.messages import SystemMessage, HumanMessage
from langgraph.graph import StateGraph, START, END
from langgraph.checkpoint.memory import MemorySaver
from langgraph.store.memory import InMemoryStore
from langgraph.types import interrupt, Command
from rich.console import Console
from rich.prompt import Prompt
from rich.panel import Panel
from rich.syntax import Syntax
from rich.tree import Tree

# --- Logging Setup ---
logging.basicConfig(
    filename='agent_pipeline.log',
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("AgenticPipeline")

console = Console()
MEMORY_FILE = ".architect_memory.json"
RULES_FILE = ".agentrules"

# --- System Prompts & Roles ---
SYSTEM_PROMPTS = {
    "architect": (
        "You are a Principal Software Architect. Your job is to analyze requirements, "
        "enforce project rules, and design robust architectures. "
        "Strict Constraint: We must never work on more than one script at the same time."
    ),
    "prompter": (
        "You are a Technical Product Manager. Your job is to translate architectural "
        "decisions into strict acceptance criteria. You MUST mandate that the code includes "
        "a `if __name__ == '__main__':` block with explicit `assert` statements to self-test the logic."
    ),
    "builder": (
        "You are a Senior Software Engineer. You write clean, optimized code fulfilling specifications. "
        "CRITICAL: You must include a `if __name__ == '__main__':` block at the bottom with `assert` "
        "statements to validate your logic. Output ONLY raw code inside triple backticks."
    )
}

# --- Utility Functions ---
def get_external_imports(code: str) -> set:
    """Scans generated code for uninstalled third-party packages."""
    try:
        tree = ast.parse(code)
    except SyntaxError:
        return set()
    
    imports = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            for alias in node.names: imports.add(alias.name.split('.')[0])
        elif isinstance(node, ast.ImportFrom):
            if node.module: imports.add(node.module.split('.')[0])
            
    stdlib = sys.stdlib_module_names if hasattr(sys, 'stdlib_module_names') else set()
    return {imp for imp in imports if imp not in stdlib and imp not in sys.builtin_module_names}

class RepositoryAnalyzer:
    @staticmethod
    def get_repo_context() -> tuple[str, Tree]:
        """Uses Git to get all tracked and untracked (but not ignored) files."""
        res = subprocess.run(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard"], 
            capture_output=True, text=True
        )
        
        if res.returncode != 0 or not res.stdout.strip():
            return "Empty repository.", Tree("[bold cyan]📦 Empty Repository[/bold cyan]")
            
        file_paths = res.stdout.strip().split('\n')
        text_summary = "Existing files in repository:\n" + "\n".join([f"- {p}" for p in file_paths])
        
        tree = Tree("\n[bold cyan]📦 Current Repository[/bold cyan]")
        nodes = {'': tree}
        
        for path in sorted(file_paths):
            parts = path.split('/')
            current_path = ''
            for i, part in enumerate(parts):
                parent_path = current_path
                current_path = f"{current_path}/{part}" if current_path else part
                
                if current_path not in nodes:
                    is_file = (i == len(parts) - 1)
                    parent_node = nodes[parent_path]
                    if is_file:
                        nodes[current_path] = parent_node.add(f"[green]📄 {part}[/green]")
                    else:
                        nodes[current_path] = parent_node.add(f"[bold blue]📂 {part}[/bold blue]")
                        
        return text_summary, tree

class SystemPreflight:
    @staticmethod
    def run_checks():
        logger.info("Initializing pre-flight checks.")
        console.print("[bold cyan]Running System Pre-flight Checks...[/bold cyan]")
        
        res = subprocess.run(["git", "rev-parse", "--is-inside-work-tree"], capture_output=True, text=True)
        if res.returncode != 0:
            logger.error("Git check failed: Not inside a repository.")
            console.print("[bold red]Error: Not inside a Git repository. Run 'git init' first.[/bold red]")
            sys.exit(1)
            
        status = subprocess.run(["git", "status", "--porcelain"], capture_output=True, text=True)
        if status.stdout.strip():
            logger.error("Git check failed: Dirty working tree.")
            console.print("[bold red]Error: Working directory has uncommitted changes. Stash or commit first.[/bold red]")
            sys.exit(1)
            
        res = subprocess.run(["ollama", "list"], capture_output=True, text=True)
        if "qwen2.5-coder:7b" not in res.stdout:
            logger.error("Ollama check failed: qwen2.5-coder:7b not found.")
            console.print("[bold red]Error: qwen2.5-coder:7b not found. Run 'ollama pull qwen2.5-coder:7b'[/bold red]")
            sys.exit(1)
        console.print("✅ System ready.\n")

def load_disk_memory(store: InMemoryStore):
    if os.path.exists(MEMORY_FILE):
        try:
            with open(MEMORY_FILE, "r") as f:
                data = json.load(f)
                store.put(("architecture", "preferences"), "global", {"data": data.get("data", "")})
                logger.info("Loaded architectural memory from disk.")
        except json.JSONDecodeError:
            logger.warning("Failed to decode memory file.")

def save_disk_memory(store: InMemoryStore):
    past_memory = store.get(("architecture", "preferences"), "global")
    if past_memory:
        with open(MEMORY_FILE, "w") as f:
            json.dump(past_memory.value, f)
            logger.info("Saved architectural memory to disk.")

def get_project_rules() -> str:
    if os.path.exists(RULES_FILE):
        with open(RULES_FILE, "r", encoding="utf-8") as f:
            return f.read().strip()
    return "No explicit project rules provided."

# --- Shared State ---
# NOTE: repo_context is removed from state; the Architect fetches it dynamically now.
class AgentState(TypedDict):
    task: str
    target_file: str
    architect_feedback: Optional[str]
    spec: str
    code: str
    test_output: Optional[str]
    iteration: int
    input_tokens: Annotated[int, operator.add]
    output_tokens: Annotated[int, operator.add]

llm = ChatOllama(model="qwen2.5-coder:7b", temperature=0.1)

def extract_tokens(response) -> dict:
    usage = response.usage_metadata or {}
    return {
        "input_tokens": usage.get("input_tokens", 0),
        "output_tokens": usage.get("output_tokens", 0)
    }

def clean_extracted_code(raw_content: str) -> str:
    match = re.search(r"```(?:python)?\s*(.*?)\s*```", raw_content, re.DOTALL | re.IGNORECASE)
    return match.group(1).strip() if match else raw_content.strip()

# --- Nodes ---

def architect_agent(state: AgentState, store: InMemoryStore) -> dict:
    logger.info(f"Architect starting task: {state['task']}")
    start_time = time.perf_counter()
    
    # Fetch repository context dynamically inside the node
    repo_text, repo_tree = RepositoryAnalyzer.get_repo_context()
    console.print(repo_tree)
    
    with console.status("[bold blue]Architect is analyzing memory and scoping the task...[/bold blue]"):
        past_memory = store.get(("architecture", "preferences"), "global")
        memory_context = past_memory.value["data"] if past_memory else "No historical decisions."
        
        human_prompt = (
            f"Task: {state['task']}\n"
            f"Repository Structure:\n{repo_text}\n"
            f"Static Project Rules:\n{get_project_rules()}\n\n"
            f"Historical Decisions:\n{memory_context}\n\n"
            "Analyze the best approach. Generate 2 technical questions for the user regarding architecture."
        )
        
        res = llm.invoke([SystemMessage(content=SYSTEM_PROMPTS["architect"]), HumanMessage(content=human_prompt)])
        tokens = extract_tokens(res)
    
    elapsed = time.perf_counter() - start_time
    logger.info(f"Architect finished in {elapsed:.2f}s. Input tokens: {tokens['input_tokens']}, Output tokens: {tokens['output_tokens']}")
    
    console.print(Panel(res.content, title=f"[bold blue]Architect Analysis ({elapsed:.2f}s)[/bold blue]"))
    
    # This interrupt natively triggers the chat-box in LangGraph Studio
    human_decision = interrupt("Provide your design decisions based on the questions above: ")
    
    logger.info(f"Human architectural feedback received: {human_decision}")
    store.put(("architecture", "preferences"), "global", {"data": human_decision})
    save_disk_memory(store) 
    return {"architect_feedback": human_decision, "iteration": 0, **tokens}

def prompter_agent(state: AgentState) -> dict:
    logger.info("Prompter generating specifications.")
    start_time = time.perf_counter()
    with console.status("[bold magenta]Prompter is drafting strict acceptance criteria...[/bold magenta]"):
        human_prompt = (
            f"Task: {state['task']}\nTarget File: {state['target_file']}\n"
            f"Architectural Feedback:\n{state['architect_feedback']}\n"
            "Return acceptance criteria, edge cases, and mandate a self-testing assert block."
        )
        res = llm.invoke([SystemMessage(content=SYSTEM_PROMPTS["prompter"]), HumanMessage(content=human_prompt)])
        tokens = extract_tokens(res)
        
    elapsed = time.perf_counter() - start_time
    logger.info(f"Prompter finished in {elapsed:.2f}s.")
    console.print(f"[dim magenta]⏱️ Prompter finished in {elapsed:.2f}s[/dim magenta]")
    return {"spec": res.content, **tokens}

def builder_agent(state: AgentState) -> dict:
    logger.info(f"Builder writing code for {state['target_file']} (Iteration {state['iteration']}).")
    start_time = time.perf_counter()
    with console.status("[bold green]Builder is writing code...[/bold green]"):
        feedback_context = f"\nExecution Errors to Fix:\n{state['test_output']}" if state.get("test_output") else ""
        human_prompt = (
            f"Target file: {state['target_file']}\n"
            f"Project Rules:\n{get_project_rules()}\n\n"
            f"Specification:\n{state['spec']}\n{feedback_context}\n"
        )
        
        res = llm.invoke([SystemMessage(content=SYSTEM_PROMPTS["builder"]), HumanMessage(content=human_prompt)])
        code = clean_extracted_code(res.content)
        
        old_code = ""
        if os.path.exists(state["target_file"]):
            with open(state["target_file"], "r", encoding="utf-8") as f:
                old_code = f.read()
                
        with open(state["target_file"], "w", encoding="utf-8") as f:
            f.write(code)
            
        diff = list(difflib.unified_diff(old_code.splitlines(), code.splitlines(), lineterm=""))
        logger.debug(f"File diff for {state['target_file']}:\n" + "\n".join(diff))
        
        tokens = extract_tokens(res)
        
    elapsed = time.perf_counter() - start_time
    logger.info(f"Builder finished in {elapsed:.2f}s.")
    console.print(f"[dim green]⏱️ Builder finished in {elapsed:.2f}s[/dim green]")
    return {"code": code, "iteration": state["iteration"] + 1, **tokens}

def validator_agent(state: AgentState) -> dict:
    logger.info("Validator initiating dependency checks and sandbox testing.")
    start_time = time.perf_counter()
    
    deps = get_external_imports(state["code"])
    if deps:
        logger.warning(f"External dependencies detected: {deps}")
        feedback = interrupt(f"⚠️ External dependencies detected: {', '.join(deps)}. Please run 'pip install' for them in another terminal, then type 'done' to continue (or 'skip'): ")
        logger.info(f"Human dependency resolution: {feedback}")

    with console.status("[bold yellow]Validator is running self-tests...[/bold yellow]"):
        try:
            res = subprocess.run(["python", state["target_file"]], capture_output=True, text=True, timeout=15)
            elapsed = time.perf_counter() - start_time
            if res.returncode == 0:
                logger.info(f"Validation passed successfully in {elapsed:.2f}s.")
                console.print(f"[bold green]✅ Validation Passed (Exit Code 0) ({elapsed:.2f}s).[/bold green]")
                return {"test_output": None}
            
            logger.error(f"Validation failed (Exit {res.returncode}): {res.stderr.strip()}")
            console.print(f"[bold red]❌ Validation Failed (Exit Code {res.returncode}) ({elapsed:.2f}s). Returning to Builder.[/bold red]")
            return {"test_output": f"STDERR:\n{res.stderr}\nSTDOUT:\n{res.stdout}"}
            
        except subprocess.TimeoutExpired:
            logger.error("Validation failed: TimeoutExpired (15s)")
            console.print("[bold red]❌ Validation Failed (Timeout). Infinite loop detected.[/bold red]")
            return {"test_output": "Execution timed out after 15 seconds. Code likely contains an infinite loop."}

def human_verification(state: AgentState) -> dict:
    logger.info("Awaiting final human verification.")
    syntax = Syntax(state['code'], "python", theme="monokai", line_numbers=True)
    console.print(Panel(syntax, title=f"[bold green]Proposed Code: {state['target_file']}[/bold green]"))
    
    diff = subprocess.run(["git", "diff", state['target_file']], capture_output=True, text=True).stdout
    if diff:
        console.print(Panel(diff, title="[bold yellow]Git Diff[/bold yellow]"))
        
    feedback = interrupt("Type 'approve' to push PR, or provide instructions for changes: ")
    
    if feedback.lower().strip() != "approve":
        logger.info(f"Human rejected code with feedback: {feedback}")
        return {"test_output": f"Human Rejected: {feedback}"}
    
    logger.info("Human approved code.")
    return {}

def shipper_agent(state: AgentState) -> dict:
    logger.info("Shipper initiating branch and PR creation.")
    start_time = time.perf_counter()
    file = state["target_file"]
    branch = f"agent-patch-{file.replace('.', '-')}-{int(time.time())}"
    
    with console.status("[bold cyan]Shipper is committing and opening PR...[/bold cyan]"):
        subprocess.run(["git", "checkout", "-B", branch], capture_output=True, check=True)
        subprocess.run(["git", "add", file], capture_output=True, check=True)
        subprocess.run(["git", "commit", "-m", f"feat(agent): implement {file}"], capture_output=True, check=True)
        subprocess.run(["git", "push", "-u", "origin", branch], capture_output=True, check=True)
        
    console.print("[bold cyan]Opening Pull Request...[/bold cyan]")
    pr_result = subprocess.run([
        "gh", "pr", "create", 
        "--title", f"feat: automated implementation of {file}", 
        "--body", f"Automated implementation for task: {state['task']}\n\nGenerated by local agent stack."
    ], capture_output=True, text=True)
    
    subprocess.run(["git", "checkout", "-"], capture_output=True)
    
    elapsed = time.perf_counter() - start_time
    logger.info(f"Shipper finished in {elapsed:.2f}s. PR Output: {pr_result.stdout.strip()}")
    
    console.print(f"\n[bold green]✅ Pull Request successfully created for branch: {branch} ({elapsed:.2f}s)[/bold green]")
    if pr_result.stdout:
        console.print(f"[bold blue]PR Link: {pr_result.stdout.strip()}[/bold blue]")
    return {}

# --- Routing Logic ---
def route_after_validation(state: AgentState) -> str:
    if state["iteration"] >= 4: 
        logger.warning("Max iterations reached. Forcing human verification.")
        return "human_verification"
    if state.get("test_output"): return "builder"
    return "human_verification"

def route_after_verification(state: AgentState) -> str:
    if state.get("test_output"): return "builder"
    return "shipper"

# --- Graph Assembly ---
workflow = StateGraph(AgentState)
workflow.add_node("architect", architect_agent)
workflow.add_node("prompter", prompter_agent)
workflow.add_node("builder", builder_agent)
workflow.add_node("validator", validator_agent)
workflow.add_node("human_verification", human_verification)
workflow.add_node("shipper", shipper_agent)

workflow.add_edge(START, "architect")
workflow.add_edge("architect", "prompter")
workflow.add_edge("prompter", "builder")
workflow.add_edge("builder", "validator")
workflow.add_conditional_edges("validator", route_after_validation)
workflow.add_conditional_edges("human_verification", route_after_verification)
workflow.add_edge("shipper", END)

memory_store = InMemoryStore()
load_disk_memory(memory_store)

app = workflow.compile(checkpointer=MemorySaver(), store=memory_store)

# --- CLI Execution (Ignored by LangGraph Studio) ---
if __name__ == "__main__":
    try:
        logger.info("=== NEW AGENT PIPELINE SESSION START ===")
        SystemPreflight.run_checks()
        
        task_input = Prompt.ask("[bold white]Enter the task description[/bold white]")
        file_input = Prompt.ask("[bold white]Enter the target file name[/bold white]", default="app.py")
        
        thread_config = {"configurable": {"thread_id": f"dev_session_{int(time.time())}"}}
        
        initial_state = {
            "task": task_input,
            "target_file": file_input,
            "iteration": 0,
            "input_tokens": 0,
            "output_tokens": 0
        }
        
        for _ in app.stream(initial_state, config=thread_config):
            pass
            
        while True:
            current_state = app.get_state(thread_config)
            
            state_vals = current_state.values
            if 'input_tokens' in state_vals:
                console.print(f"[dim cyan]Total Tokens Spent -> Input: {state_vals['input_tokens']} | Output: {state_vals['output_tokens']}[/dim cyan]")
            
            if not current_state.next:
                logger.info("Pipeline completed successfully.")
                console.print("\n[bold green]🚀 Pipeline Complete.[/bold green]")
                break
                
            pending_msg = current_state.tasks[0].interrupts[0].value
            user_input = Prompt.ask(f"\n[bold yellow]{pending_msg}[/bold yellow]")
            
            for _ in app.stream(Command(resume=user_input), config=thread_config):
                pass

    except KeyboardInterrupt:
        logger.warning("Pipeline interrupted by user (KeyboardInterrupt).")
        console.print("\n[bold red]Pipeline interrupted by user. Exiting gracefully.[/bold red]")
        sys.exit(0)