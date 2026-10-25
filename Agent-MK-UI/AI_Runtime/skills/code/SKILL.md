---
name: code
description: Implement a focused code change from provided source or an attached project.
keywords: code coding implement feature refactor function bug fix build modify source
priority: 12
---
Use complete source pasted in chat as the working context for standalone code. For attached-project changes, inspect only the relevant instructions and call path. Keep behavior and public interfaces unless asked to change them. Make the smallest complete fix, handle failure cases, and return the full updated file when the user needs a script. Do not invent unseen dependencies or claim execution was verified when it was not.