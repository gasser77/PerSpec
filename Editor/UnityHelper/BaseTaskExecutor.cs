using System.Collections.Generic;
using UnityEngine;

namespace PerSpec.UnityHelper.Editor
{
    public abstract class BaseTaskExecutor : ITaskExecutor
    {
        public enum ExecutorType
        {
            SCENE,
            LOCALIZATION,
            TMPRO
        }

        public abstract ExecutorType Type { get; }

        public abstract bool Execute(Task task);

        /// <summary>
        /// Override this to return true for tasks that run asynchronously.
        /// When true, ScenarioRunner will wait for task.asyncCompleted before continuing.
        /// </summary>
        public virtual bool IsAsyncTask(Task task) => false;

        /// <summary>
        /// Called by async executors when their async operation completes.
        /// Sets the task's async completion flags.
        /// </summary>
        protected void CompleteAsyncTask(Task task, bool success, string error = "")
        {
            task.asyncSuccess = success;
            task.asyncCompleted = true;
            if (!success && !string.IsNullOrEmpty(error))
            {
                task.error = error;
            }
            Debug.Log($"[BaseTaskExecutor] Async task completed: {task.action}, success={success}");
        }

        protected string GetParam(Task task, string key)
        {
            if (task.parameters != null)
            {
                var param = task.parameters.Find(p => p.key == key);
                if (param != null)
                    return param.value ?? string.Empty;
            }
            return null;
        }

        protected string GetOptionalParam(Task task, string key, string defaultValue = "")
        {
            if (task.parameters != null)
            {
                var param = task.parameters.Find(p => p.key == key);
                return param?.value ?? defaultValue;
            }
            return defaultValue;
        }

    }
}
