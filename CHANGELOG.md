# Changelog

All notable changes to Phoenix Controls are documented here.

**v1.0** is the first public release — the starting point. From here on, each
release lists what's **New**, **Fixed**, and **Gone** in plain language.

---

## Hotfix - 1.0.2606301 - 2026.06.30

**New**
- Architect and Visualist now have a built-in node reference — open it to read what every node does, with its inputs and outputs, right inside the app.
- Processes keep running once started — any triggers you place on a process (schedules, chat commands, events) stay active until the process is stopped, instead of running once and quitting.
- The same process can run as several copies at once, each with its own start values.
- The Start and Stop nodes turn a process on and off from your logic.

**Fixed**
- Architect's left-rail Add, Rename, and Delete buttons for variables, macros, and processes now open their dialog every time.
- Architect's confirm prompts, recent files, save checks, keyboard shortcuts, and variable trace now open reliably.
- Visualist's media library, media picker, new layer, shape editor, and curve editor now open reliably.
- Process and Macro nodes now pick their target from a dropdown, instead of a free-text box that often didn't match and broke the saved script.
- Architect Process, Macro, and Event node pins connect to wires the moment you place the node, instead of only after saving and reopening the graph.
- Wires stay attached to an Architect node when you resize it, instead of drifting outside its body.
- Architect's node search lists the closest matches first and finds nodes by their title, and node tooltips show again on hover.
- An event's trigger and executor keep their added inputs and return values in sync across files.
- Long value pills on Architect nodes wrap to fit instead of spilling past the node edge.
