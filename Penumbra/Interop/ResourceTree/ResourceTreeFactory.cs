using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Penumbra.Api.Enums;
using Penumbra.Collections;
using Penumbra.GameData.Actors;
using Penumbra.Interop.PathResolving;
using Penumbra.Services;
using Penumbra.String.Classes;

namespace Penumbra.Interop.ResourceTree;

public class ResourceTreeFactory
{
    private readonly IDataManager       _gameData;
    private readonly IObjectTable       _objects;
    private readonly CollectionResolver _collectionResolver;
    private readonly IdentifierService  _identifier;
    private readonly Configuration      _config;
    private readonly ActorService       _actors;

    public ResourceTreeFactory(IDataManager gameData, IObjectTable objects, CollectionResolver resolver, IdentifierService identifier,
        Configuration config, ActorService actors)
    {
        _gameData           = gameData;
        _objects            = objects;
        _collectionResolver = resolver;
        _identifier         = identifier;
        _config             = config;
        _actors             = actors;
    }

    private TreeBuildCache CreateTreeBuildCache(bool withCharacters)
        => new(_objects, _gameData, _actors, withCharacters);

    public IEnumerable<Dalamud.Game.ClientState.Objects.Types.Character> GetLocalPlayerRelatedCharacters()
    {
        var cache = CreateTreeBuildCache(true);

        return cache.Characters.Where(cache.IsLocalPlayerRelated);
    }

    public IEnumerable<(Dalamud.Game.ClientState.Objects.Types.Character Character, ResourceTree ResourceTree)> FromObjectTable(
        Flags flags)
    {
        var cache      = CreateTreeBuildCache(true);
        var characters = (flags & Flags.LocalPlayerRelatedOnly) != 0 ? cache.Characters.Where(cache.IsLocalPlayerRelated) : cache.Characters;

        foreach (var character in characters)
        {
            var tree = FromCharacter(character, cache, flags);
            if (tree != null)
                yield return (character, tree);
        }
    }

    public IEnumerable<(Dalamud.Game.ClientState.Objects.Types.Character Character, ResourceTree ResourceTree)> FromCharacters(
        IEnumerable<Dalamud.Game.ClientState.Objects.Types.Character> characters, Flags flags)
    {
        var cache = CreateTreeBuildCache((flags & Flags.WithOwnership) != 0);
        foreach (var character in characters)
        {
            var tree = FromCharacter(character, cache, flags);
            if (tree != null)
                yield return (character, tree);
        }
    }

    public ResourceTree? FromCharacter(Dalamud.Game.ClientState.Objects.Types.Character character, Flags flags)
        => FromCharacter(character, CreateTreeBuildCache((flags & Flags.WithOwnership) != 0), flags);

    private unsafe ResourceTree? FromCharacter(Dalamud.Game.ClientState.Objects.Types.Character character, TreeBuildCache cache, Flags flags)
    {
        if (!character.IsValid())
            return null;

        var gameObjStruct = (GameObject*)character.Address;
        var drawObjStruct = gameObjStruct->GetDrawObject();
        if (drawObjStruct == null)
            return null;

        var collectionResolveData = _collectionResolver.IdentifyCollection(gameObjStruct, true);
        if (!collectionResolveData.Valid)
            return null;

        var localPlayerRelated = cache.IsLocalPlayerRelated(character);
        var (name, related)    = GetCharacterName(character, cache);
        var networked          = character.ObjectId != Dalamud.Game.ClientState.Objects.Types.GameObject.InvalidGameObjectId;
        var tree = new ResourceTree(name, character.ObjectIndex, (nint)gameObjStruct, (nint)drawObjStruct, localPlayerRelated, related, networked, collectionResolveData.ModCollection.Name);
        var globalContext = new GlobalResolveContext(_identifier.AwaitedService, cache,
            ((Character*)gameObjStruct)->CharacterData.ModelCharaId, (flags & Flags.WithUIData) != 0);
        tree.LoadResources(globalContext);
        tree.FlatNodes.UnionWith(globalContext.Nodes.Values);
        tree.ProcessPostfix((node, _) => tree.FlatNodes.Add(node));

        ResolveGamePaths(tree, collectionResolveData.ModCollection);
        if (globalContext.WithUiData)
            ResolveUiData(tree);
        FilterFullPaths(tree, (flags & Flags.RedactExternalPaths) != 0 ? _config.ModDirectory : null);
        Cleanup(tree);

        return tree;
    }

    private static void ResolveGamePaths(ResourceTree tree, ModCollection collection)
    {
        var forwardSet = new HashSet<Utf8GamePath>();
        var reverseSet = new HashSet<string>();
        foreach (var node in tree.FlatNodes)
        {
            if (node.PossibleGamePaths.Length == 0 && !node.FullPath.InternalName.IsEmpty)
                reverseSet.Add(node.FullPath.ToPath());
            else if (node.FullPath.InternalName.IsEmpty && node.PossibleGamePaths.Length == 1)
                forwardSet.Add(node.GamePath);
        }

        var forwardDictionary    = forwardSet.ToDictionary(path => path, collection.ResolvePath);
        var reverseArray         = reverseSet.ToArray();
        var reverseResolvedArray = collection.ReverseResolvePaths(reverseArray);
        var reverseDictionary    = reverseArray.Zip(reverseResolvedArray).ToDictionary(pair => pair.First, pair => pair.Second);

        foreach (var node in tree.FlatNodes)
        {
            if (node.PossibleGamePaths.Length == 0 && !node.FullPath.InternalName.IsEmpty)
            {
                if (reverseDictionary.TryGetValue(node.FullPath.ToPath(), out var resolvedSet))
                {
                    var resolvedList = resolvedSet.ToList();
                    if (resolvedList.Count > 1)
                    {
                        var filteredList = node.ResolveContext!.FilterGamePaths(resolvedList);
                        if (filteredList.Count > 0)
                            resolvedList = filteredList;
                    }
                    if (resolvedList.Count != 1)
                    {
                        Penumbra.Log.Information($"Found {resolvedList.Count} game paths while reverse-resolving {node.FullPath} in {collection.Name}:");
                        foreach (var gamePath in resolvedList)
                            Penumbra.Log.Information($"Game path: {gamePath}");
                    }
                    node.PossibleGamePaths = resolvedList.ToArray();
                }
            }
            else if (node.FullPath.InternalName.IsEmpty && node.PossibleGamePaths.Length == 1)
            {
                if (forwardDictionary.TryGetValue(node.GamePath, out var resolved))
                    node.FullPath = resolved ?? new FullPath(node.GamePath);
            }
        }
    }

    private static void ResolveUiData(ResourceTree tree)
    {
        foreach (var node in tree.FlatNodes)
        {
            if (node.Name != null || node.PossibleGamePaths.Length == 0)
                continue;

            var gamePath = node.PossibleGamePaths[0];
            node.SetUiData(node.Type switch
            {
                ResourceType.Imc => node.ResolveContext!.GuessModelUIData(gamePath).PrependName("IMC: "),
                ResourceType.Mdl => node.ResolveContext!.GuessModelUIData(gamePath),
                _                => node.ResolveContext!.GuessUIDataFromPath(gamePath),
            });
        }

        tree.ProcessPostfix((node, parent) =>
        {
            if (node.Name == parent?.Name)
                node.Name = null;
        });
    }

    private static void FilterFullPaths(ResourceTree tree, string? onlyWithinPath)
    {
        static bool ShallKeepPath(FullPath fullPath, string? onlyWithinPath)
        {
            if (!fullPath.IsRooted)
                return true;

            if (onlyWithinPath != null)
            {
                var relPath = Path.GetRelativePath(onlyWithinPath, fullPath.FullName);
                if (relPath != "." && (relPath.StartsWith('.') || Path.IsPathRooted(relPath)))
                    return false;
            }

            return fullPath.Exists;
        }

        foreach (var node in tree.FlatNodes)
        {
            if (!ShallKeepPath(node.FullPath, onlyWithinPath))
                node.FullPath = FullPath.Empty;
        }
    }

    private static void Cleanup(ResourceTree tree)
    {
        foreach (var node in tree.FlatNodes)
        {
            node.Name ??= node.FallbackName;

            node.FallbackName   = null;
            node.ResolveContext = null;
        }
    }

    private unsafe (string Name, bool PlayerRelated) GetCharacterName(Dalamud.Game.ClientState.Objects.Types.Character character,
        TreeBuildCache cache)
    {
        var    identifier = _actors.AwaitedService.FromObject((GameObject*)character.Address, out var owner, true, false, false);
        string name;
        bool   playerRelated;
        switch (identifier.Type)
        {
            case IdentifierType.Player:
                name          = identifier.PlayerName.ToString();
                playerRelated = true;
                break;
            case IdentifierType.Owned when cache.CharactersById.TryGetValue(owner->ObjectID, out var ownerChara):
                var ownerName = GetCharacterName(ownerChara, cache);
                name          = $"[{ownerName.Name}] {character.Name} ({identifier.Kind.ToName()})";
                playerRelated = ownerName.PlayerRelated;
                break;
            default:
                name          = $"{character.Name} ({identifier.Kind.ToName()})";
                playerRelated = false;
                break;
        }

        return (name, playerRelated);
    }

    [Flags]
    public enum Flags
    {
        RedactExternalPaths    = 1,
        WithUIData             = 2,
        LocalPlayerRelatedOnly = 4,
        WithOwnership          = 8,
    }
}
