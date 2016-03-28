# es-workflow

Workflow tooling for developers using CSharp.

*Under Development*

## Goal

Provide a automation suite around a specific development team workflow that minmizes fuss while enforcing top quality software development practices

## Overview

This workflow requires the developers to follow strict conventions around source code layout. The `.sln` and `.csproj` files used to build the code are *not* checked into vcs, and are instead generated by `Es.Vsg.exe` using hint files and layout convention.

The use of the `git bash` command line is assumed, and a set of bash functions is provided for dealing with the creation and management of project and feature branches.

The repository structure must be as follows:

```
/version.txt (one line, the Major.Minor.Patch version. Always include all three numbers)
/sln.vsg
/AssemblyName/csproj.vsg
/AssemblyName/source.cs
```

If an assembly directory contains a `Program.cs` file it will be built as an executable.

The `sln.vsg` file is a json object where you can specify the sln name to use and the nuget packages to install. For packages tha support multiple architectures you must also include the subdirectory of the lib folder to use.

```
{
  "name":"SolutionName",
  "packages": [
    ["Nunit","2.6.4"],
	["Newtonsoft.Json","8.0.3","net45"]
  ]
}
```

The `csproj.vsg` file is a json object where you can specify the references needed by the csproj.
```
{
 "refs": [
  "Newtonsoft.Json",
  "System",
  "System.Core"
 ]
}
```

Generated csproj files will have warnings as errors, unsafe code, and code analysis turned on. Output will be directed to an `o` directory, and the debugging working directoy will be set to the solution directory.

If an assembly directory contains a `pack.nup` file the build system will publish the assembly as a nuget package on everybuild, using the version.txt file for the version number, the branch prefix as the prerelease tag, and the build number as the prerelease number.

Branches must follow the convention assumed by the tools. The convention is as follows:

`master` is the branch shipped to production. No direct changes are made here.

Project branches match `/^[a-zA-Z]+[0-9]+\./`. That is they start with a tag and a number. Tags should be short (1-4 letters is recommended), and differ by development group. The number is the project number and should always increase as new projects are started. The use of different tags by different teams is to avoid having to assign project numbers globally.

For example: `core1.lemon` would be the first project for the `core` team given a codename of `lemon`. All projects should use code names and avoid any name that would indicate a shipping order or the scope of the project as experience has shown those are not likely to remain constant during the projects lifetime.

Feature branches match `/^[a-zA-Z]+[0-9]+[a-zA-Z]+[0-9]+\./`. The same as a project branch with more more tag and number. This is usually `f` and the ticket number of the feature. Feature numbers are unique across a team (not per project).

For example: `f1core1.update_readme` would be the branch for working on ticket 1 or the core team.

You can continue to prepend tag# to the branch to create more child branches (x1f1core1 for an experiment in feature 1 or project core 1). Generally two levels is enough for most people though.
