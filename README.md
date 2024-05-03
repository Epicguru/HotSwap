# HotSwap
Code hotswapping utility for the RimWorld game.  

This is a fork of HotSwap that has the following changes compared to the original:
 - The ability to hot swap all methods without having to annotate each class.
 - Automatic reloading of assemblies when changes to tracked `.dll` files are detected.
 - The ability to exclude certain methods from hot swapping even if they would otherwise be included (some complex methods always throw errors).

## Usage

1. Add the following attribute classes to your mod project, somewhere:
```C#
public sealed class HotSwapAllAttribute : Attribute { }
public sealed class HotSwapAttribute : Attribute { }
public sealed class IgnoreHotSwapAttribute : Attribute { }
```

2. Use them as follows:
```C#
// Reload all methods within this class:
[HotSwap]
public class MyClass { /* ... */ }

// Reload all methods in every class in this assembly:
[HotSwapAll]
public class MyClass2 { /* ... */ }

[HotSwap]
public class MyClass3
{
  // This method will not be hot swapped.
  [IgnoreHotSwap]
  void MyMethod(){}
}
```

3. Install this HotSwap mod.
4. In-game, there is a new hot swap button in the developer tools hotbar. Click to reload assemblies.
5. Assemblies will also be reloaded automatically when .dll changes are detected.

## Known issues
 - Sometimes automatic reloading does not trigger. Click the button in-game if you are unsure.
 - While most changes are supported, some source code changes such as adding or removing fields or methods cannot be hot swapped and will result in errors.
   That can include making changes that cause the compiler to generate new classes or methods behind the scenes, such as IEnumerable yield statements, editing Linq etc.
   - If you make changes and get errors when hot swapping, you will need to re-launch the game in order for your changes to apply.
