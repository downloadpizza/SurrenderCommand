# Installing

You need BepinEx 5 installed for Nuclear Option and [Muj's Command Mod](https://github.com/muji2498/CommandMod)

Just throw the dll into your plugins folder. Now whenever you are host, there will be a !surrender and !nosurrender command available to everyone.

# Surrendering

Run !surrender to start a vote.

# Config

The Host can modify: 
- How long the game needs to be running before a surrender vote can be started.
- What percentage of the team is required for a surrender.
- How much cooldown between surrenders
- How long to wait to default people to "nosurrender"

Check the relevant BepinEx/configs file

# Contributing

Just add your game folder in GameDir.targets and make sure you have the CommandMod in your bepin folder. Then it **should** dotnet build correctly.
