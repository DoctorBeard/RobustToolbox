using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Robust.Client.GameObjects
{
    /// <summary>
    ///     Handles interpolation of transform positions.
    /// </summary>
    [UsedImplicitly]
    public sealed partial class TransformSystem : SharedTransformSystem
    {
        // Max distance per tick how far an entity can move before it is considered teleporting.
        // TODO: Make these values somehow dependent on server TPS.
        private const float MaxInterpolationDistance = 2.0f;
        private const float MaxInterpolationDistanceSquared = MaxInterpolationDistance * MaxInterpolationDistance;

        private const float MinInterpolationDistance = 0.001f;
        private const float MinInterpolationDistanceSquared = MinInterpolationDistance * MinInterpolationDistance;

        private const double MinInterpolationAngle = Math.PI / 720;

        // 45 degrees.
        private const double MaxInterpolationAngle = Math.PI / 4;

        [Dependency] private readonly IGameTiming _gameTiming = default!;

        // Only keep track of transforms actively lerping.
        // Much faster than iterating 3000+ transforms every frame.
        [ViewVariables] private readonly List<TransformComponent> _lerpingTransforms = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<TransformStartLerpMessage>(TransformStartLerpHandler);
        }

        private void TransformStartLerpHandler(TransformStartLerpMessage ev)
        {
            _lerpingTransforms.Add(ev.Transform);
        }

        public override void FrameUpdate(float frameTime)
        {
            base.FrameUpdate(frameTime);

            var step = (float) (_gameTiming.TickRemainder.TotalSeconds / _gameTiming.TickPeriod.TotalSeconds);

            for (var i = 0; i < _lerpingTransforms.Count; i++)
            {
                var transform = _lerpingTransforms[i];
                var found = false;

                // Only lerp if parent didn't change.
                // E.g. entering lockers would do it.
                if (transform.LerpParent == transform.ParentUid &&
                    transform.ParentUid.IsValid())
                {
                    if (transform.LerpDestination != null)
                    {
                        var lerpDest = transform.LerpDestination.Value;
                        var lerpSource = transform.LerpSource;
                        var distance = (lerpDest - lerpSource).LengthSquared;

                        if (distance is > MinInterpolationDistanceSquared and < MaxInterpolationDistanceSquared)
                        {
                            transform.LocalPosition = Vector2.Lerp(lerpSource, lerpDest, step);
                            // Setting LocalPosition clears LerpPosition so fix that.
                            transform.LerpDestination = lerpDest;
                            found = true;
                        }
                    }

                    if (transform.LerpAngle != null)
                    {
                        var lerpDest = transform.LerpAngle.Value;
                        var lerpSource = transform.LerpSourceAngle;
                        var distance = Math.Abs(Angle.ShortestDistance(lerpDest, lerpSource));

                        if (distance is > MinInterpolationAngle and < MaxInterpolationAngle)
                        {
                            transform.LocalRotation = Angle.Lerp(lerpSource, lerpDest, step);
                            // Setting LocalRotation clears LerpAngle so fix that.
                            transform.LerpAngle = lerpDest;
                            found = true;
                        }
                    }
                }

                // Transforms only get removed from the lerp list if they no longer are in here.
                // This is much easier than having the transform itself tell us to remove it.
                if (!found)
                {
                    // Transform is no longer lerping, remove.
                    transform.ActivelyLerping = false;
                    _lerpingTransforms.RemoveSwap(i);
                    i -= 1;
                }
            }
        }
    }
}
