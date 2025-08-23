using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.BDD {
    [AttributeUsage(AttributeTargets.Method)]
    public class FirebaseMethodAttribute : Attribute, ITrackableAttribute {
        public string TableName { get; }
        public string Data { get; }
        public FirebaseOperation Operation { get; }
        public string DocumentId { get; }
        public string[] Fields { get; }

        public FirebaseMethodAttribute(string tableName, string data = "", 
            FirebaseOperation operation = FirebaseOperation.Read, 
            string documentId = "", params string[] fields) => 
            (TableName, Data, Operation, DocumentId, Fields) = (tableName, data, operation, documentId, fields);

        public void Execute(object instance, MethodInfo method) {
            UnityEngine.Debug.Log($"[Firebase] {instance?.GetType().Name}.{method?.Name} -> Table: {TableName}, Operation: {Operation}");
            FirebaseManager.Instance?.ExecuteFirebaseOperation(this, instance);
        }
    }

    public enum FirebaseOperation { Create, Read, Update, Delete, Query, Listen }

    public static class FirebaseTracker {
        public static void Log(object instance, [CallerMemberName] string methodName = "") => 
            AttributeTracker<FirebaseMethodAttribute>.Track(instance, methodName);
    }
}
