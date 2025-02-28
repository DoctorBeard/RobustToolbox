using System.Collections.Generic;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.Containers
{
    /// <summary>
    /// Base container class that all container inherit from.
    /// </summary>
    public abstract class BaseContainer : IContainer
    {
        /// <inheritdoc />
        [ViewVariables]
        public abstract IReadOnlyList<EntityUid> ContainedEntities { get; }

        [ViewVariables]
        public abstract List<EntityUid> ExpectedEntities { get; }

        /// <inheritdoc />
        public abstract string ContainerType { get; }

        /// <inheritdoc />
        [ViewVariables]
        public bool Deleted { get; private set; }

        /// <inheritdoc />
        [ViewVariables]
        public string ID { get; internal set; } = default!; // Make sure you set me in init

        /// <inheritdoc />
        public IContainerManager Manager { get; internal set; } = default!; // Make sure you set me in init

        /// <inheritdoc />
        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("occludes")]
        public bool OccludesLight { get; set; } = true;

        /// <inheritdoc />
        [ViewVariables]
        public EntityUid Owner => Manager.Owner;

        /// <inheritdoc />
        [ViewVariables(VVAccess.ReadWrite)]
        [DataField("showEnts")]
        public bool ShowContents { get; set; }

        /// <summary>
        /// DO NOT CALL THIS METHOD DIRECTLY!
        /// You want <see cref="IContainerManager.MakeContainer{T}(string)" /> instead.
        /// </summary>
        protected BaseContainer() { }

        /// <inheritdoc />
        public bool Insert(EntityUid toinsert, IEntityManager? entMan = null, TransformComponent? transform = null, TransformComponent? ownerTransform = null, MetaDataComponent? meta = null)
        {
            DebugTools.Assert(!Deleted);
            DebugTools.Assert(transform == null || transform.Owner == toinsert);
            DebugTools.Assert(ownerTransform == null || ownerTransform.Owner == Owner);
            DebugTools.Assert(meta == null || meta.Owner == toinsert);
            IoCManager.Resolve(ref entMan);

            //Verify we can insert into this container
            if (!CanInsert(toinsert, entMan))
                return false;

            transform ??= entMan.GetComponent<TransformComponent>(toinsert);

            // CanInsert already checks nullability of Parent (or container forgot to call base that does)
            if (toinsert.TryGetContainerMan(out var containerManager, entMan) && !containerManager.Remove(toinsert))
                return false; // Can't remove from existing container, can't insert.

            // Update metadata first, so that parent change events can check IsInContainer.
            meta ??= entMan.GetComponent<MetaDataComponent>(toinsert);
            meta.Flags |= MetaDataFlags.InContainer;

            ownerTransform ??= entMan.GetComponent<TransformComponent>(Owner);
            var oldParent = transform.ParentUid;
            transform.AttachParent(ownerTransform);
            InternalInsert(toinsert, oldParent, entMan);

            // This is an edge case where the parent grid is the container being inserted into, so AttachParent would not unanchor.
            if (transform.Anchored)
                transform.Anchored = false;

            // spatially move the object to the location of the container. If you don't want this functionality, the
            // calling code can save the local position before calling this function, and apply it afterwords.
            transform.LocalPosition = Vector2.Zero;
            transform.LocalRotation = Angle.Zero;

            return true;
        }

        /// <inheritdoc />
        public virtual bool CanInsert(EntityUid toinsert, IEntityManager? entMan = null)
        {
            DebugTools.Assert(!Deleted);

            // cannot insert into itself.
            if (Owner == toinsert)
                return false;

            IoCManager.Resolve(ref entMan);

            // no, you can't put maps or grids into containers
            if (entMan.HasComponent<IMapComponent>(toinsert) || entMan.HasComponent<IMapGridComponent>(toinsert))
                return false;

            var xformSystem = entMan.EntitySysManager.GetEntitySystem<SharedTransformSystem>();
            var xformQuery = entMan.GetEntityQuery<TransformComponent>();

            // Crucial, prevent circular insertion.
            if (xformSystem.ContainsEntity(xformQuery.GetComponent(toinsert), Owner, xformQuery))
                return false;

            //Improvement: Traverse the entire tree to make sure we are not creating a loop.

            //raise events
            var insertAttemptEvent = new ContainerIsInsertingAttemptEvent(this, toinsert);
            entMan.EventBus.RaiseLocalEvent(Owner, insertAttemptEvent, true);
            if (insertAttemptEvent.Cancelled)
                return false;

            var gettingInsertedAttemptEvent = new ContainerGettingInsertedAttemptEvent(this, toinsert);
            entMan.EventBus.RaiseLocalEvent(toinsert, gettingInsertedAttemptEvent, true);
            if (gettingInsertedAttemptEvent.Cancelled)
                return false;

            return true;
        }

        /// <inheritdoc />
        public bool Remove(EntityUid toremove, IEntityManager? entMan = null, TransformComponent? xform = null, MetaDataComponent? meta = null)
        {
            DebugTools.Assert(!Deleted);
            DebugTools.AssertNotNull(Manager);
            DebugTools.AssertNotNull(toremove);
            IoCManager.Resolve(ref entMan);
            DebugTools.Assert(entMan.EntityExists(toremove));
            DebugTools.Assert(xform == null || xform.Owner == toremove);

            if (!CanRemove(toremove, entMan)) return false;
            InternalRemove(toremove, entMan, meta);

            xform ??= entMan.GetComponent<TransformComponent>(toremove);
            xform.AttachParentToContainerOrGrid(entMan);
            return true;
        }

        /// <inheritdoc />
        public void ForceRemove(EntityUid toRemove, IEntityManager? entMan = null, MetaDataComponent? meta = null)
        {
            DebugTools.Assert(!Deleted);
            DebugTools.AssertNotNull(Manager);
            DebugTools.AssertNotNull(toRemove);
            IoCManager.Resolve(ref entMan);
            DebugTools.Assert(entMan.EntityExists(toRemove));

            InternalRemove(toRemove, entMan, meta);
        }

        /// <inheritdoc />
        public virtual bool CanRemove(EntityUid toremove, IEntityManager? entMan = null)
        {
            DebugTools.Assert(!Deleted);

            if (!Contains(toremove))
                return false;

            IoCManager.Resolve(ref entMan);

            //raise events
            var removeAttemptEvent = new ContainerIsRemovingAttemptEvent(this, toremove);
            entMan.EventBus.RaiseLocalEvent(Owner, removeAttemptEvent, true);
            if (removeAttemptEvent.Cancelled)
                return false;

            var gettingRemovedAttemptEvent = new ContainerGettingRemovedAttemptEvent(this, toremove);
            entMan.EventBus.RaiseLocalEvent(toremove, gettingRemovedAttemptEvent, true);
            if (gettingRemovedAttemptEvent.Cancelled)
                return false;

            return true;
        }

        /// <inheritdoc />
        public abstract bool Contains(EntityUid contained);

        /// <inheritdoc />
        public virtual void Shutdown()
        {
            Manager.InternalContainerShutdown(this);
            Deleted = true;
        }

        /// <summary>
        /// Implement to store the reference in whatever form you want
        /// </summary>
        /// <param name="toinsert"></param>
        /// <param name="entMan"></param>
        protected virtual void InternalInsert(EntityUid toinsert, EntityUid oldParent, IEntityManager entMan)
        {
            DebugTools.Assert(!Deleted);
            entMan.EventBus.RaiseLocalEvent(Owner, new EntInsertedIntoContainerMessage(toinsert, oldParent, this), true);
            Manager.Dirty(entMan);
        }

        /// <summary>
        /// Implement to remove the reference you used to store the entity
        /// </summary>
        /// <param name="toremove"></param>
        /// <param name="entMan"></param>
        protected virtual void InternalRemove(EntityUid toremove, IEntityManager entMan, MetaDataComponent? meta = null)
        {
            DebugTools.Assert(!Deleted);
            DebugTools.AssertNotNull(Manager);
            DebugTools.AssertNotNull(toremove);
            DebugTools.Assert(entMan.EntityExists(toremove));
            DebugTools.Assert(meta == null || meta.Owner == toremove);

            meta ??= entMan.GetComponent<MetaDataComponent>(toremove);
            meta.Flags &= ~MetaDataFlags.InContainer;
            entMan.EventBus.RaiseLocalEvent(Owner, new EntRemovedFromContainerMessage(toremove, this), true);
            entMan.EventBus.RaiseLocalEvent(toremove, new EntGotRemovedFromContainerMessage(toremove, this), false);
            Manager.Dirty(entMan);
        }
    }
}
