using System.Collections.Generic;

namespace PerSpec.UnityHelper.Editor
{
    public static class TaskExecutorRegistry
    {
        private static readonly Dictionary<BaseTaskExecutor.ExecutorType, ITaskExecutor> _executors = new();

        public static void Register(ITaskExecutor executor)
        {
            _executors[executor.Type] = executor;
        }

        public static bool ExecuteTask(Task task)
        {
            if (_executors.TryGetValue(task.type, out var executor))
            {
                return executor.Execute(task);
            }

            task.error = $"No executor registered for type: {task.type}";
            return false;
        }

        /// <summary>
        /// Expose IsAsyncTask so non-UI coordinators can respect async tasks.
        /// Casts to BaseTaskExecutor since IsAsyncTask is defined there (not on ITaskExecutor).
        /// </summary>
        public static bool IsAsyncTask(Task task)
        {
            if (_executors.TryGetValue(task.type, out var executor))
            {
                if (executor is BaseTaskExecutor bte)
                    return bte.IsAsyncTask(task);
            }
            return false;
        }
    }
}
