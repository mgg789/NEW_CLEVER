# CLEV3R Basic Plus: compact guide

This guide is an orientation aid for Codex. The installed `Help/<language>` XML files,
`Operators.txt`, and tracked `Examples` are the source of truth for exact API names,
parameters, return values, and supported syntax. Use `clev3r.docs_search` before writing
an unfamiliar robot API call, then use `clev3r.docs_read` for the complete topic.

## Files and program structure

- `.bp` is an executable program. Top-level statements run in order.
- `.bpi` is inserted with `include "relative/path"` (the extension is omitted).
- `.bpm` is imported with `import "relative/path"` (the extension is omitted).
- Paths are relative to the file containing the directive. See the shipped
  `Examples/Include` and `Examples/New_Path_Examples` projects.
- A comment begins with `'`.

Variables are created by assignment. The runtime supports number and string values;
arrays and object-specific values are documented by the corresponding Help topic.
The shipped examples also use string values such as `"True"` where a runtime method
expects a textual boolean, so copy the form documented for that method.

```basic
message = "Hello"
count = 3
LCD.Clear()
LCD.Write(20, 40, message)
Program.Delay(1000)
```

## Control flow

```basic
If count > 0 Then
  count--
ElseIf count = 0 Then
  count = 10
Else
  count = 1
EndIf

For i = 0 To 10 Step 2
  LCD.Write(0, i * 8, i)
EndFor

While "True"
  Program.Delay(100)
EndWhile
```

`Break` and `Continue` are demonstrated in
`Examples/BreakAndContinue/BreakAndContinueTest.bp`. Consult the keyword and operator
topics for the exact supported forms and comparison operators.

## Subroutines and functions

Subroutines have no parameter list in the shipped examples and end with `EndSub`.
Functions declare typed `in` and `out` parameters and end with `EndFunction`.

```basic
ShowMessage

Sub ShowMessage
  LCD.Write(0, 0, "Ready")
EndSub

result = 0
Add(2, 3, result)

Function Add(in number left, in number right, out number value)
  value = left + right
EndFunction
```

Use `@name` inside a function to access a global variable only when necessary; its
semantics are documented in `Operators.txt`.

## Motors and sensors

There are two API styles in the shipped material. Prefer the generic, currently
documented object APIs unless an existing project consistently uses port-specific
objects such as `MotorA` or `Sensor1`.

```basic
' Motor ports are strings such as "A" or "AB".
Motor.Start("AB", 50)
Program.Delay(1000)
Motor.Stop("AB", "True")

' Sensor ports are numbers 1 through 4.
Sensor.SetMode(1, 0)
value = Sensor.ReadPercent(1)
```

Before choosing a sensor mode or motor operation, search the `Motor`, `Sensor`, and
port-specific topics. Do not infer port assignments from wheel diameter, wheelbase,
or the free-form robot description; obtain the live robot context and project code.

## Safe agent workflow

1. Read the workspace and live robot context immediately before robot work.
2. Search documentation for every unfamiliar keyword, object, or method.
3. Edit only files inside the active project root.
4. Compile after changing BP code and resolve reported diagnostics.
5. Treat compile, deploy, and run as separate operations. A successful compile does
   not mean the program was uploaded or started.
6. Report deployment or execution as successful only after the corresponding EV3
   tool returns success.

