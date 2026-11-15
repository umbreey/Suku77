using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Umbreey
{
    public abstract class EntityBase : MonoBehaviour
    {
        private EntityDataBoard _dataBoard;
        private List<EntityComponent> _components;
        private bool _enabled;

        protected virtual void Awake()
        {
            InternalReset();
            enabled = false;
        }
        
        protected virtual void OnEnable() => Enable();
        protected virtual void OnDisable() => Disable();
        protected virtual void OnDestroy() => InternalDestroy();
        
        public void Enable()
        {
            if (_enabled)
                return;
            
            _enabled = true;
            foreach (var i in _components)
            {
                EntityComponent.InternalOnEnable(i);
            }
            
            enabled = true; // mono too
        }

        public void Disable()
        {
            if (!_enabled)
                return;

            _enabled = false;
            foreach (var i in _components)
            {
                EntityComponent.InternalOnDisable(i);
            }
            
            _dataBoard.Reset();
            enabled = false; // mono too
        }

        private void  InternalDestroy()
        {
            Disable();
            
            foreach (var i in _components)
            {
                EntityComponent.InternalOnDestroy(i);
            }
            _dataBoard.Destroy();
        }
        
        private void InternalReset()
        {
            _components = new List<EntityComponent>();
            _dataBoard = new EntityDataBoard(this);
            _enabled = false;
        }
        
        public List<EntityComponent> GetEntityComponents()
        {
            return _components;
        }
        
        public T AddEntityComponent<T>(params object[] parameters) where T : EntityComponent
        {
            EntityComponent target = AddEntityComponent(typeof(T), parameters);
            return target as T;
        }

        public T GetEntityComponent<T>() where T: EntityComponent
        {
            return (T)GetEntityComponent(typeof(T));
        }
        
        public void RemoveEntityComponent<T>() where T : EntityComponent
        {
            RemoveEntityComponent(typeof(T));
        }

        public EntityComponent AddEntityComponent(Type type, params object[] parameters)
        {
            if (type == null)
                return null;
            
            EntityComponent target = GetEntityComponent(type);
            if (target == null)
            {
                target = (EntityComponent)Activator.CreateInstance(type, parameters);
                _components.Add(target);
                EntityComponent.InternalAwake(target, _dataBoard);
                EntityComponent.InternalStart(target);
                
                if (_enabled)
                {
                    EntityComponent.InternalOnEnable(target);
                }
            }

            return target;
        }
        
        public EntityComponent GetEntityComponent(Type type)
        {
            if (_components == null || _components.Count == 0)
            {
                return null;
            }
            
            foreach (var component in _components)
            {
                var compType = component.GetType();
                if (compType == type || compType.IsSubclassOf(type))
                {
                    return component;
                }
            }
            
            return null;
        }
        
        public void RemoveEntityComponent(Type type)
        {
            if (type == null)
                return;
            
            EntityComponent target = GetEntityComponent(type);
            if (target != null)
            {
                EntityComponent.InternalOnDisable(target);
                EntityComponent.InternalOnDestroy(target);
                
                _components.Remove(target);
            }
        }
        
        public void ClearEntityComponents()
        {
            InternalDestroy();
            InternalReset();
        }
        
        public T Get<T>() where T : IEntityData
        {
            return _dataBoard.Get<T>();
        }
        
        
        public T TryGet<T>() where T : IEntityData
        {
            if (_dataBoard == null) return default;
            return _dataBoard.TryGet<T>();
        }
        
        private void FixedUpdate()
        {
            float deltaTime = Time.fixedDeltaTime;
            foreach (var component in _components)
            {
                EntityComponent.InternalFixedUpdate(component, deltaTime);
            }
        }

        protected virtual void Update()
        {
            float deltaTime = Time.deltaTime;
            foreach (var component in _components)
            {
                EntityComponent.InternalUpdate(component, deltaTime);
            }
        }

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            foreach (var component in _components)
            {
                EntityComponent.InternalLateUpdate(component, deltaTime);
            }
        }
    }
}