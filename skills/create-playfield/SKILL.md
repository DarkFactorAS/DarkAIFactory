---
name: create-playfield
description: >-

  The purpose of this skill is to create a playfield in the BOTS game.
  All the functions to create a playfield and create objects can be done from 
  code in this repository.
  The playfield consists of the start location for 2-4 robots (no game has only
  one robot), 1-5 goals where the robot should be able to path to. The playfield
  also has pickups that robots can collect during the game.
  Your task is to create a playfield with all the necessary elements for the BOTS game.
  The size of the playfield can vary from small (5x5) to large (20x20) grids.
---

## When To Use
   When the user wants to create a new playfield

## Info
  In this repository, playfields are not hand-written JSON. They are binary resource files stored in Assets/Resources/PlayfieldData and named like R-<uuid>.bytes for standard resource maps, or T-<uuid>.bytes for test maps. They must be serialized using the same DFBinaryStream format used by the game client.

  The loader expects a resource playfield to have:
  - version = 11
  - sourceType = Resource (16)
  - uuid, localRevisionId, cloudRevisionId, creator name, display name, description
  - NumPlayers, NumGoals, BoardSizeX, BoardSizeY
  - a boolean hasData = true
  - a tile data list that starts with the object count, then entries of: tileType, rotation, x, y

   Serialization must match the engine writer exactly. The canonical implementation is
   PlayfieldData.CreateBinaryData(), which writes PlayfieldConstants.CurrentVersion (11)
   and uses DFBinaryStream for all fields. DFBinaryStream writes UInt8, UInt16, and
   UInt32 values in big-endian order. Therefore the version 11 header must begin with
   the bytes 00 0B. Writing 0B 00 with a little-endian serializer makes the loader read
   the version as 2816 and reject the playfield.

  The canonical resource loader is Assets/Gamecode/Modules/PlayfieldModule/PlayfieldLoader/PlayfieldLoader/PlayfieldDataResourceLoader.cs.

  Tiles are stored on stacked layers, bottom to top:
  1. Floor
  2. Conveyors
  3. Spawn points and goals
  4. Walls
  5. Pickups and other interaction tiles

  Each layer keeps its own tile list keyed by grid position (see PlayfieldObjectData.AddTile), so a grid cell with a spawn point, goal, wall, or pickup still needs its own separate Floor-layer tile entry at the same x,y. Every grid position on the board must have a floor tile beneath it, even where another layer's object is placed there.

## Procedure

1. Decide the playfield size and gameplay layout.
   - Typical testing sizes are small (5x5 to 8x8) or medium (10x10 to 12x16).
   - Ensure the board supports at least 2 players and valid pathing.

2. Generate a unique UUID and file name.
   - For a normal resource playfield, create the file as Assets/Resources/PlayfieldData/R-<uuid>.bytes.
   - Use T-<uuid>.bytes only for test playfields that are intentionally marked as test maps.

3. Create the binary header.
    - Create the data through PlayfieldData.CreateBinaryData() whenever possible; do not
       hand-write a parallel binary format.
    - Write PlayfieldData version 11 using DFBinaryStream's big-endian UInt16 writer.
       Verify the first two bytes are 00 0B and that the parsed version is 11, not 2816.
   - Write SourceType.Resource (16).
   - Write uuid, localRevisionId, cloudRevisionId, creator name, display name, description, flags, NumPlayers, NumGoals, BoardSizeX, BoardSizeY.
   - For newly created local resource playfields, localRevisionId and cloudRevisionId are typically 0.
   - Add the hasData flag as true.

4. Create the floor layer.
   - Add a floor tile at every grid position across the full board, with no exceptions.
   - Keep the board rectangular and avoid disconnected or inaccessible sections.
   - Do not skip the floor tile under cells that will later get a spawn point, goal, wall, or pickup — those are separate layers stacked on top of the floor tile, not a replacement for it.

5. Place valid spawn points.
   - Use FloorTile_SpawnPoint1 through SpawnPoint4 in order.
   - Ensure the sequence is complete and non-duplicate for the chosen player count.
   - For a test arena, 2 players is the minimum viable setup.

6. Place goals.
   - Use FloorTile_Goal1 through Goal4/Goal5 in sequence.
   - Keep at least one goal and ensure robot pathing can reach it.

7. Add optional pickups and obstacles.
   - Pickups can be health, shield, lightning, etc. if the arena is meant to teach or test mechanics.
   - Keep the route open enough for robot navigation.

8. Serialize the tile list.
   - Write the total number of playfield objects as a uint16 before the first entry.
   - Each entry is written as: tileType, rotation, x, y.
   - The game expects the list as uint16 values in the same order used when reading in Playfield.PopulateTilesV7().

9. Register the resource playfield.
   - Add a constant in PlayfieldDataResourceLoader, e.g. RESOURCE_PLAYFIELD_THOR_AI_TEST.
   - Preload user-visible resource maps in LoadPlayfieldList() so they appear in the normal resource list.
   - Use LoadTestPlayfield() only for dedicated gameplay-test maps.

10. Validate the result.
   - Load the map through PlayfieldDataResourceLoader.LoadPlayfield(uuid).
   - Confirm the header parses correctly, the map spawns, and the gameplay state is valid.
   - Before testing in Unity, inspect the first two bytes of the file and confirm they
     are 00 0B. Also parse the file with PlayfieldData.CreateFromBinaryStream and
     confirm Version == 11.

11. Keep the result reproducible.
   - Store the final file under Assets/Resources/PlayfieldData and keep the UUID constant in the resource loader for future tests.
   - Keep the Unity-generated .bytes.meta file alongside the .bytes asset so the resource keeps a stable GUID.

## Important Notes
- The project does not use a custom playfield JSON format for resource battles; it uses the binary serialized format directly.
- A valid playfield must be readable by PlayfieldData.CreateFromBinaryStream and must have tile data that matches the current version (11).
- Never use little-endian struct packing for playfield integers; it changes version 11
   into 2816 and can corrupt every subsequent field interpretation.
- If the playfield is meant for tests or AI, keep the arena simple, symmetric, and pathable so the behavior is deterministic.
