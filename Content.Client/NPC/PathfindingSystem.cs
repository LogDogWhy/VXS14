using System.Linq;
using System.Numerics;
using System.Text;
using Content.Shared.NPC;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.NPC
{
    public sealed class PathfindingSystem : SharedPathfindingSystem
    {
        [Dependency] private readonly IEyeManager _eyeManager = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IInputManager _inputManager = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly IResourceCache _cache = default!;
        [Dependency] private readonly NPCSteeringSystem _steering = default!;

        public PathfindingDebugMode Modes
        {
            get => _modes;
            set
            {
                var overlayManager = IoCManager.Resolve<IOverlayManager>();

                if (value == PathfindingDebugMode.None)
                {
                    Breadcrumbs.Clear();
                    Polys.Clear();
                    overlayManager.RemoveOverlay<PathfindingOverlay>();
                }
                else if (!overlayManager.HasOverlay<PathfindingOverlay>())
                {
                    overlayManager.AddOverlay(new PathfindingOverlay(EntityManager, _eyeManager, _inputManager, _mapManager, _cache, this));
                }

                if ((value & PathfindingDebugMode.Steering) != 0x0)
                {
                    _steering.DebugEnabled = true;
                }
                else
                {
                    _steering.DebugEnabled = false;
                }

                _modes = value;

                RaiseNetworkEvent(new RequestPathfindingDebugMessage()
                {
                    Mode = _modes,
                });
            }
        }

        private PathfindingDebugMode _modes = PathfindingDebugMode.None;

        // It's debug data IDC if it doesn't support snapshots I just want something fast.
        public Dictionary<EntityUid, Dictionary<Vector2i, List<PathfindingBreadcrumb>>> Breadcrumbs = new();
        public Dictionary<EntityUid, Dictionary<Vector2i, Dictionary<Vector2i, List<DebugPathPoly>>>> Polys = new();
        public readonly List<(TimeSpan Time, PathRouteMessage Message)> Routes = new();

        public override void Initialize()
        {
            base.Initialize();
            SubscribeNetworkEvent<PathBreadcrumbsMessage>(OnBreadcrumbs);
            SubscribeNetworkEvent<PathBreadcrumbsRefreshMessage>(OnBreadcrumbsRefresh);
            SubscribeNetworkEvent<PathPolysMessage>(OnPolys);
            SubscribeNetworkEvent<PathPolysRefreshMessage>(OnPolysRefresh);
            SubscribeNetworkEvent<PathRouteMessage>(OnRoute);
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            if (!_timing.IsFirstTimePredicted)
                return;

            for (var i = 0; i < Routes.Count; i++)
            {
                var route = Routes[i];

                if (_timing.RealTime < route.Time)
                    break;

                Routes.RemoveAt(i);
            }
        }

        private void OnRoute(PathRouteMessage ev)
        {
            Routes.Add((_timing.RealTime + TimeSpan.FromSeconds(0.5), ev));
        }

        private void OnPolys(PathPolysMessage ev)
        {
            Polys = ev.Polys;
        }

        private void OnPolysRefresh(PathPolysRefreshMessage ev)
        {
            var chunks = Polys.GetOrNew(ev.GridUid);
            chunks[ev.Origin] = ev.Polys;
        }

        public override void Shutdown()
        {
            base.Shutdown();
            // Don't send any messages to server, just shut down quietly.
            _modes = PathfindingDebugMode.None;
        }

        private void OnBreadcrumbs(PathBreadcrumbsMessage ev)
        {
            Breadcrumbs = ev.Breadcrumbs;
        }

        private void OnBreadcrumbsRefresh(PathBreadcrumbsRefreshMessage ev)
        {
            if (!Breadcrumbs.TryGetValue(ev.GridUid, out var chunks))
                return;

            chunks[ev.Origin] = ev.Data;
        }
    }

    public sealed class PathfindingOverlay : Overlay
    {
        private readonly IEntityManager _entManager;
        private readonly IEyeManager _eyeManager;
        private readonly IInputManager _inputManager;
        private readonly IMapManager _mapManager;
        private readonly PathfindingSystem _system;

        public override OverlaySpace Space => OverlaySpace.ScreenSpace | OverlaySpace.WorldSpace;

        private readonly Font _font;

        public PathfindingOverlay(
            IEntityManager entManager,
            IEyeManager eyeManager,
            IInputManager inputManager,
            IMapManager mapManager,
            IResourceCache cache,
            PathfindingSystem system)
        {
            _entManager = entManager;
            _eyeManager = eyeManager;
            _inputManager = inputManager;
            _mapManager = mapManager;
            _system = system;
            _font = new VectorFont(cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 10);
        }

        protected override void Draw(in OverlayDrawArgs args)
        {
            switch (args.DrawingHandle)
            {
                case DrawingHandleScreen screenHandle:
                    DrawScreen(args, screenHandle);
                    break;
                case DrawingHandleWorld worldHandle:
                    DrawWorld(args, worldHandle);
                    break;
            }
        }

        private void DrawScreen(OverlayDrawArgs args, DrawingHandleScreen screenHandle)
        {
            var mousePos = _inputManager.MouseScreenPosition;
            var mouseWorldPos = _eyeManager.PixelToMap(mousePos);
            var aabb = new Box2(mouseWorldPos.Position - SharedPathfindingSystem.ChunkSizeVec, mouseWorldPos.Position + SharedPathfindingSystem.ChunkSizeVec);
            var xformQuery = _entManager.GetEntityQuery<TransformComponent>();

            if ((_system.Modes & PathfindingDebugMode.Crumb) != 0x0 &&
                mouseWorldPos.MapId == args.MapId)
            {
                var found = false;

                foreach (var grid in _mapManager.FindGridsIntersecting(mouseWorldPos.MapId, aabb))
                {
                    if (found || !_system.Breadcrumbs.TryGetValue(grid.Owner, out var crumbs) || !xformQuery.TryGetComponent(grid.Owner, out var gridXform))
                        continue;

                    var (_, _, worldMatrix, invWorldMatrix) = gridXform.GetWorldPositionRotationMatrixWithInv();
                    var localAABB = invWorldMatrix.TransformBox(aabb.Enlarged(float.Epsilon - SharedPathfindingSystem.ChunkSize));

                    foreach (var chunk in crumbs)
                    {
                        if (found)
                            continue;

                        var origin = chunk.Key * SharedPathfindingSystem.ChunkSize;

                        var chunkAABB = new Box2(origin, origin + SharedPathfindingSystem.ChunkSize);

                        if (!chunkAABB.Intersects(localAABB))
                            continue;

                        PathfindingBreadcrumb? nearest = null;
                        var nearestDistance = float.MaxValue;

                        foreach (var crumb in chunk.Value)
                        {
                            var crumbMapPos = worldMatrix.Transform(_system.GetCoordinate(chunk.Key, crumb.Coordinates));
                            var distance = (crumbMapPos - mouseWorldPos.Position).Length();

                            if (distance < nearestDistance)
                            {
                                nearestDistance = distance;
                                nearest = crumb;
                            }
                        }

                        if (nearest != null)
                        {
                            var text = new StringBuilder();

                            // Sandbox moment
                            var coords = $"Point coordinates: {nearest.Value.Coordinates.ToString()}";
                            var gridCoords =
                                $"Grid coordinates: {_system.GetCoordinate(chunk.Key, nearest.Value.Coordinates).ToString()}";
                            var layer = $"Layer: {nearest.Value.Data.CollisionLayer.ToString()}";
                            var mask = $"Mask: {nearest.Value.Data.CollisionMask.ToString()}";

                            text.AppendLine(coords);
                            text.AppendLine(gridCoords);
                            text.AppendLine(layer);
                            text.AppendLine(mask);
                            text.AppendLine($"Flags:");

                            foreach (var flag in Enum.GetValues<PathfindingBreadcrumbFlag>())
                            {
                                if ((flag & nearest.Value.Data.Flags) == 0x0)
                                    continue;

                                var flagStr = $"- {flag.ToString()}";
                                text.AppendLine(flagStr);
                            }

                            screenHandle.DrawString(_font, mousePos.Position, text.ToString());
                            found = true;
                            break;
                        }
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.Poly) != 0x0 &&
                mouseWorldPos.MapId == args.MapId)
            {
                if (!_mapManager.TryFindGridAt(mouseWorldPos, out var gridUid, out var grid) || !xformQuery.TryGetComponent(gridUid, out var gridXform))
                    return;

                if (!_system.Polys.TryGetValue(gridUid, out var data))
                    return;

                var tileRef = grid.GetTileRef(mouseWorldPos);
                var localPos = tileRef.GridIndices;
                var chunkOrigin = localPos / SharedPathfindingSystem.ChunkSize;

                if (!data.TryGetValue(chunkOrigin, out var chunk) ||
                    !chunk.TryGetValue(new Vector2i(localPos.X % SharedPathfindingSystem.ChunkSize,
                        localPos.Y % SharedPathfindingSystem.ChunkSize), out var tile))
                {
                    return;
                }

                var invGridMatrix = gridXform.InvWorldMatrix;
                DebugPathPoly? nearest = null;
                var nearestDistance = float.MaxValue;

                foreach (var poly in tile)
                {
                    if (poly.Box.Contains(invGridMatrix.Transform(mouseWorldPos.Position)))
                    {
                        nearest = poly;
                        break;
                    }
                }

                if (nearest != null)
                {
                    var text = new StringBuilder();
                    /*

                    // Sandbox moment
                    var coords = $"Point coordinates: {nearest.Value.Coordinates.ToString()}";
                    var gridCoords =
                        $"Grid coordinates: {_system.GetCoordinate(chunk.Key, nearest.Value.Coordinates).ToString()}";
                    var layer = $"Layer: {nearest.Value.Data.CollisionLayer.ToString()}";
                    var mask = $"Mask: {nearest.Value.Data.CollisionMask.ToString()}";

                    text.AppendLine(coords);
                    text.AppendLine(gridCoords);
                    text.AppendLine(layer);
                    text.AppendLine(mask);
                    text.AppendLine($"Flags:");

                    foreach (var flag in Enum.GetValues<PathfindingBreadcrumbFlag>())
                    {
                        if ((flag & nearest.Value.Data.Flags) == 0x0)
                            continue;

                        var flagStr = $"- {flag.ToString()}";
                        text.AppendLine(flagStr);
                    }

                    foreach (var neighbor in )

                    screenHandle.DrawString(_font, mousePos.Position, text.ToString());
                    found = true;
                    break;
                    */
                }
            }
        }

        private void DrawWorld(OverlayDrawArgs args, DrawingHandleWorld worldHandle)
        {
            var mousePos = _inputManager.MouseScreenPosition;
            var mouseWorldPos = _eyeManager.PixelToMap(mousePos);
            var aabb = new Box2(mouseWorldPos.Position - Vector2.One / 4f, mouseWorldPos.Position + Vector2.One / 4f);
            var xformQuery = _entManager.GetEntityQuery<TransformComponent>();

            if ((_system.Modes & PathfindingDebugMode.Breadcrumbs) != 0x0 &&
                mouseWorldPos.MapId == args.MapId)
            {
                foreach (var grid in _mapManager.FindGridsIntersecting(mouseWorldPos.MapId, aabb))
                {
                    if (!_system.Breadcrumbs.TryGetValue(grid.Owner, out var crumbs) ||
                        !xformQuery.TryGetComponent(grid.Owner, out var gridXform))
                    {
                        continue;
                    }

                    var (_, _, worldMatrix, invWorldMatrix) = gridXform.GetWorldPositionRotationMatrixWithInv();
                    worldHandle.SetTransform(worldMatrix);
                    var localAABB = invWorldMatrix.TransformBox(aabb);

                    foreach (var chunk in crumbs)
                    {
                        var origin = chunk.Key * SharedPathfindingSystem.ChunkSize;

                        var chunkAABB = new Box2(origin, origin + SharedPathfindingSystem.ChunkSize);

                        if (!chunkAABB.Intersects(localAABB))
                            continue;

                        foreach (var crumb in chunk.Value)
                        {
                            if (crumb.Equals(PathfindingBreadcrumb.Invalid))
                            {
                                continue;
                            }

                            const float edge = 1f / SharedPathfindingSystem.SubStep / 4f;
                            var edgeVec = new Vector2(edge, edge);

                            var masked = crumb.Data.CollisionMask != 0 || crumb.Data.CollisionLayer != 0;
                            Color color;

                            if ((crumb.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0x0)
                            {
                                color = Color.Green;
                            }
                            else if (masked)
                            {
                                color = Color.Blue;
                            }
                            else
                            {
                                color = Color.Orange;
                            }

                            var coordinate = _system.GetCoordinate(chunk.Key, crumb.Coordinates);
                            worldHandle.DrawRect(new Box2(coordinate - edgeVec, coordinate + edgeVec), color.WithAlpha(0.25f));
                        }
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.Polys) != 0x0 &&
                mouseWorldPos.MapId == args.MapId)
            {
                foreach (var grid in _mapManager.FindGridsIntersecting(args.MapId, aabb))
                {
                    if (!_system.Polys.TryGetValue(grid.Owner, out var data) ||
                        !xformQuery.TryGetComponent(grid.Owner, out var gridXform))
                        continue;

                    var (_, _, worldMatrix, invWorldMatrix) = gridXform.GetWorldPositionRotationMatrixWithInv();
                    worldHandle.SetTransform(worldMatrix);
                    var localAABB = invWorldMatrix.TransformBox(aabb);

                    foreach (var chunk in data)
                    {
                        var origin = chunk.Key * SharedPathfindingSystem.ChunkSize;

                        var chunkAABB = new Box2(origin, origin + SharedPathfindingSystem.ChunkSize);

                        if (!chunkAABB.Intersects(localAABB))
                            continue;

                        foreach (var tile in chunk.Value)
                        {
                            foreach (var poly in tile.Value)
                            {
                                worldHandle.DrawRect(poly.Box, Color.Green.WithAlpha(0.25f));
                                worldHandle.DrawRect(poly.Box, Color.Red, false);
                            }
                        }
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.PolyNeighbors) != 0x0 &&
                mouseWorldPos.MapId == args.MapId)
            {
                foreach (var grid in _mapManager.FindGridsIntersecting(args.MapId, aabb))
                {
                    if (!_system.Polys.TryGetValue(grid.Owner, out var data) ||
                        !xformQuery.TryGetComponent(grid.Owner, out var gridXform))
                        continue;

                    var (_, _, worldMatrix, invMatrix) = gridXform.GetWorldPositionRotationMatrixWithInv();
                    worldHandle.SetTransform(worldMatrix);
                    var localAABB = invMatrix.TransformBox(aabb);

                    foreach (var chunk in data)
                    {
                        var origin = chunk.Key * SharedPathfindingSystem.ChunkSize;

                        var chunkAABB = new Box2(origin, origin + SharedPathfindingSystem.ChunkSize);

                        if (!chunkAABB.Intersects(localAABB))
                            continue;

                        foreach (var tile in chunk.Value)
                        {
                            foreach (var poly in tile.Value)
                            {
                                foreach (var neighborPoly in poly.Neighbors)
                                {
                                    Color color;
                                    Vector2 neighborPos;

                                    if (neighborPoly.EntityId != poly.GraphUid)
                                    {
                                        color = Color.Green;
                                        var neighborMap = neighborPoly.ToMap(_entManager);

                                        if (neighborMap.MapId != args.MapId)
                                            continue;

                                        neighborPos = invMatrix.Transform(neighborMap.Position);
                                    }
                                    else
                                    {
                                        color = Color.Blue;
                                        neighborPos = neighborPoly.Position;
                                    }

                                    worldHandle.DrawLine(poly.Box.Center, neighborPos, color);
                                }
                            }
                        }
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.Chunks) != 0x0)
            {
                foreach (var grid in _mapManager.FindGridsIntersecting(args.MapId, args.WorldBounds))
                {
                    if (!_system.Breadcrumbs.TryGetValue(grid.Owner, out var crumbs) ||
                        !xformQuery.TryGetComponent(grid.Owner, out var gridXform))
                        continue;

                    var (_, _, worldMatrix, invWorldMatrix) = gridXform.GetWorldPositionRotationMatrixWithInv();
                    worldHandle.SetTransform(worldMatrix);
                    var localAABB = invWorldMatrix.TransformBox(args.WorldBounds);

                    foreach (var chunk in crumbs)
                    {
                        var origin = chunk.Key * SharedPathfindingSystem.ChunkSize;

                        var chunkAABB = new Box2(origin, origin + SharedPathfindingSystem.ChunkSize);

                        if (!chunkAABB.Intersects(localAABB))
                            continue;

                        worldHandle.DrawRect(chunkAABB, Color.Red, false);
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.Routes) != 0x0)
            {
                foreach (var route in _system.Routes)
                {
                    foreach (var node in route.Message.Path)
                    {
                        if (!_entManager.TryGetComponent<TransformComponent>(node.GraphUid, out var graphXform))
                            continue;

                        worldHandle.SetTransform(graphXform.WorldMatrix);
                        worldHandle.DrawRect(node.Box, Color.Orange.WithAlpha(0.10f));
                    }
                }
            }

            if ((_system.Modes & PathfindingDebugMode.RouteCosts) != 0x0)
            {
                var matrix = EntityUid.Invalid;

                foreach (var route in _system.Routes)
                {
                    var highestGScore = route.Message.Costs.Values.Max();

                    foreach (var (node, cost) in route.Message.Costs)
                    {
                        if (matrix != node.GraphUid)
                        {
                            if (!_entManager.TryGetComponent<TransformComponent>(node.GraphUid, out var graphXform))
                                continue;

                            matrix = node.GraphUid;
                            worldHandle.SetTransform(graphXform.WorldMatrix);
                        }

                        worldHandle.DrawRect(node.Box, new Color(0f, cost / highestGScore, 1f - (cost / highestGScore), 0.10f));
                    }
                }
            }

            worldHandle.SetTransform(Matrix3.Identity);
        }
    }
}
