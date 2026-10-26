---
name: website-creation
description: Create or modify small websites with a clear project structure and honest verification.
keywords: website web site frontend html css javascript typescript react vite ui responsive page
priority: 7
---

For a new website, create a clearly named child folder inside the selected workspace and keep the site files together there. Inspect existing files first; do not overwrite unrelated content. Choose the simplest stack that fits the request and the project's existing tools. For a small static site, use semantic HTML, responsive CSS, and only the JavaScript needed. Add a package manifest or framework setup when the goal calls for it, not by default.

Never deliver a placeholder page or a generic one-word heading as a finished website. If the request is broad, choose sensible defaults and make a polished, useful first version instead of asking about optional colors, layout, or features. For a shop, include at least four meaningful sample products, working add/remove/quantity controls, accurate cart totals, and an actual checkout form with contact and delivery fields. For a local demo, label the payment step clearly and state that no real card is charged; do not pretend to process real payments without a configured provider. Build the page in one coherent pass and make only focused repairs instead of rewriting all files repeatedly. Keep it simple enough to run locally, explain how to open it, and verify interactions when tools permit. Before calling a storefront complete, ensure the saved source contains a real catalog, cart event handling, quantity and total calculations, responsive styling, and checkout feedback; if any are missing, continue editing.

If the user enabled local commands, use only the available allowlisted command tool and verify the site with the relevant build/check. If commands are disabled, create the source/configuration files and give the user the exact next command needed to run or build it. Do not claim the site was launched or tested unless it was.
