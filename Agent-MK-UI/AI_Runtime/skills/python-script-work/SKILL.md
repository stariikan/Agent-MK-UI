---
name: python-script-work
description: Write, adapt, and debug standalone Python scripts using the user's source and examples.
keywords: python script scripts py standalone tkinter filedialog convert conversion transform txt input output folder path files pasted snippet
priority: 30
---
Use the conversation as the primary source. Pasted scripts, earlier code, logs, and sample output count as context; no project attachment is needed for a self-contained script. Never inspect an empty chat workspace instead of the supplied material.

If the user only says they have a script, ask one short question about what they want done. If they ask to align two scripts or match an output, compare the actual complete versions and sample format. If a needed version, function, or example is missing or truncated, name that exact item and wait; do not guess delimiters, invent behavior, or claim an unobserved rewrite will work.

When the supplied script collects files into a combined output, edit the collector output-writing block for output-format requests. Do not request the content of a collected project file merely because its name appears in the sample. If the user asks for START/END markers “for each script/file,” write one paired marker around each emitted file block, include its path in both markers when useful, and preserve the existing payload. Do not put markers only around the collector program or merely print them to the console.

For file transformations, preserve the demonstrated input format and establish only the necessary details: accepted files, parsing, output location/layout, collision handling, and errors. A path the script opens at runtime is not automatically accessible to Agent-MK; still write code that can use a file/directory dialog when that is the intended flow.

When asked to add logs, put executable log statements in the script at the requested checkpoints. For batch processing, report discovered counts, resolved paths, and per-file outcomes/errors. Keep the existing output channel and say where logs appear. Do not add a Tkinter log widget unless the user explicitly asks for logs inside the GUI. A console window and a Tkinter window are different destinations; clarify only if the request and code leave that choice genuinely ambiguous.

Return a complete runnable script when the requirements are clear. Include concise run notes and report syntax/runtime verification accurately.