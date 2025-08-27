using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Reflection;

using Firebase;
using Firebase.Auth;
using Firebase.Firestore;
using Firebase.Extensions;

using com.ImmersizeFramework.Core;

namespace com.ImmersizeFramework.BDD {
    [Serializable]
    public sealed class FirebaseQueryResult {
        public bool success;
        public string error;
        public Dictionary<string, object> data;
        public List<Dictionary<string, object>> documents;
        public string documentId;

        public FirebaseQueryResult(bool success = false) => 
            (this.success, data, documents) = (success, new Dictionary<string, object>(), new List<Dictionary<string, object>>());

        public T GetData<T>(string key, T defaultValue = default) => 
            data.TryGetValue(key, out var value) && value is T result ? result : defaultValue;

        public bool HasDocuments => documents?.Count > 0;
        public int DocumentCount => documents?.Count ?? 0;
    }

    public sealed class FirebaseManager : MonoBehaviour, IFrameworkService, IDisposable {
        [Serializable]
        public sealed class FirebaseSettings {
            public string projectId = "your-project-id";
            public string webApiKey = "your-web-api-key";
            public bool enableOfflineSupport = true;
            public bool enableLogging = true;
            public int cacheSize = 100;
        }

        public sealed class FirebaseUser {
            public string uid;
            public string email;
            public string displayName;
            public bool isEmailVerified;
            public string role;
            public string company;
            public string name;
            public Dictionary<string, object> customClaims;

            public FirebaseUser() => customClaims = new Dictionary<string, object>();
            public T GetClaim<T>(string key, T defaultValue = default) => 
                customClaims.TryGetValue(key, out var value) && value is T result ? result : defaultValue;
            public bool IsAdmin => role == "admin";
            public bool IsUser => role == "user";
        }

        public static FirebaseManager Instance { get; private set; }
        public bool IsInitialized { get; private set; }
        public int Priority => 5;
        public FirebaseUser CurrentUser { get; private set; }
        public FirebaseQueryResult LastQueryResult { get; private set; }

        [SerializeField] private FirebaseSettings settings = new();

        private FirebaseApp app;
        private FirebaseAuth auth;
        private FirebaseFirestore firestore;

        public event Action<FirebaseUser> OnUserSignedIn;
        public event Action OnUserSignedOut;
        public event Action<string> OnError;
        public event Action<FirebaseQueryResult> OnQueryCompleted;

        private void Awake() {
            if (Instance == null) {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            } else Destroy(gameObject);
        }

        public async Task InitializeAsync() {
            if (IsInitialized) return;

            try {
                app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions {
                    ProjectId = settings.projectId,
                    ApiKey = settings.webApiKey
                });

                auth = FirebaseAuth.DefaultInstance;
                firestore = FirebaseFirestore.DefaultInstance;

                if (settings.enableOfflineSupport) await firestore.EnableNetworkAsync();

                auth.StateChanged += OnAuthStateChanged;
                IsInitialized = true;

                if (settings.enableLogging) Debug.Log("[Firebase] Manager initialized successfully");
            } catch (Exception ex) {
                Debug.LogError($"[Firebase] Initialization failed: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }

        }

        public void Initialize() => _ = InitializeAsync();
        
        private void OnAuthStateChanged(object sender, EventArgs eventArgs) {
            if (auth.CurrentUser?.UserId != CurrentUser?.uid) {
                var signedIn = auth.CurrentUser != null;
                if (signedIn) {
                    CurrentUser = new FirebaseUser {
                        uid = auth.CurrentUser.UserId,
                        email = auth.CurrentUser.Email,
                        displayName = auth.CurrentUser.DisplayName,
                        isEmailVerified = auth.CurrentUser.IsEmailVerified,
                        role = "user",
                        company = "",
                        name = auth.CurrentUser.DisplayName ?? ""
                    };
                    OnUserSignedIn?.Invoke(CurrentUser);
                } else {
                    CurrentUser = null;
                    OnUserSignedOut?.Invoke();
                }
            }
        }

        public async Task<FirebaseQueryResult> SignInAsync(string email, string password) =>
            await ExecuteFirebaseOperation(async () => {
                var authResult = await auth.SignInWithEmailAndPasswordAsync(email, password);
                var user = authResult.User;
                
                return new FirebaseQueryResult(true) {
                    data = {
                        ["uid"] = user.UserId,
                        ["email"] = user.Email,
                        ["displayName"] = user.DisplayName
                    }
                };
            }, "User signed in");

        public async Task<FirebaseQueryResult> CreateUserAsync(string email, string password) =>
            await ExecuteFirebaseOperation(async () => {
                var authResult = await auth.CreateUserWithEmailAndPasswordAsync(email, password);
                var user = authResult.User;
                return new FirebaseQueryResult(true) {
                    data = {
                        ["uid"] = user.UserId,
                        ["email"] = user.Email
                    }
                };
            }, "User created");

        public void SignOut() {
            auth?.SignOut();
            if (settings.enableLogging) Debug.Log("[Firebase] User signed out");
        }

        public async Task<FirebaseQueryResult> CreateDocumentAsync(string collection, Dictionary<string, object> data, string documentId = "") =>
            await ExecuteFirebaseOperation(async () => {
                var collectionRef = firestore.Collection(collection);
                var docRef = string.IsNullOrEmpty(documentId) 
                    ? await collectionRef.AddAsync(data)
                    : collectionRef.Document(documentId);
                
                if (!string.IsNullOrEmpty(documentId)) await docRef.SetAsync(data);
                
                return new FirebaseQueryResult(true) {
                    documentId = docRef.Id,
                    data = data
                };
            }, $"Document created in {collection}");

        public async Task<FirebaseQueryResult> ReadDocumentAsync(string collection, string documentId) =>
            await ExecuteFirebaseOperation(async () => {
                var snapshot = await firestore.Collection(collection).Document(documentId).GetSnapshotAsync();
                return snapshot.Exists 
                    ? new FirebaseQueryResult(true) { data = snapshot.ToDictionary(), documentId = documentId }
                    : new FirebaseQueryResult { error = "Document not found" };
            }, $"Document read from {collection}: {documentId}");

        public async Task<FirebaseQueryResult> UpdateDocumentAsync(string collection, string documentId, Dictionary<string, object> data) =>
            await ExecuteFirebaseOperation(async () => {
                await firestore.Collection(collection).Document(documentId).UpdateAsync(data);
                return new FirebaseQueryResult(true) { data = data, documentId = documentId };
            }, $"Document updated in {collection}: {documentId}");

        public async Task<FirebaseQueryResult> DeleteDocumentAsync(string collection, string documentId) =>
            await ExecuteFirebaseOperation(async () => {
                await firestore.Collection(collection).Document(documentId).DeleteAsync();
                return new FirebaseQueryResult(true) { documentId = documentId };
            }, $"Document deleted from {collection}: {documentId}");

        public async Task<FirebaseQueryResult> QueryCollectionAsync(string collection, string field = "", object value = null, int limit = 50) =>
            await ExecuteFirebaseOperation(async () => {
                var collectionRef = firestore.Collection(collection);
                Query query = collectionRef;
                if (!string.IsNullOrEmpty(field) && value != null) query = query.WhereEqualTo(field, value);
                
                var snapshot = await query.Limit(limit).GetSnapshotAsync();
                return new FirebaseQueryResult(true) {
                    documents = snapshot.Documents.Select(doc => {
                        var data = doc.ToDictionary();
                        data["_id"] = doc.Id;
                        return data;
                    }).ToList()
                };
            }, $"Query completed for {collection}");

        private async Task<FirebaseQueryResult> ExecuteFirebaseOperation(Func<Task<FirebaseQueryResult>> operation, string logMessage) {
            try {
                var result = await operation();

                if (result.success && settings.enableLogging) Debug.Log($"[Firebase] {logMessage}");

                LastQueryResult = result;
                OnQueryCompleted?.Invoke(result);

                return result;
            } catch (Exception ex) {
                var errorResult = new FirebaseQueryResult { error = ex.Message };

                OnError?.Invoke(ex.Message);
                LastQueryResult = errorResult;
                OnQueryCompleted?.Invoke(errorResult);
                
                return errorResult;
            }
        }

        public async void ExecuteFirebaseOperation(FirebaseMethodAttribute attr, object instance) {
            try {
                switch (attr.Operation) {
                    case FirebaseOperation.Create:
                        await CreateDocumentAsync(attr.TableName, ParseDataString(attr.Data), attr.DocumentId);
                        break;
                    case FirebaseOperation.Read:
                        var documentId = GetDynamicDocumentId(attr, instance);
                        if (!string.IsNullOrEmpty(documentId)) 
                            await ReadDocumentAsync(attr.TableName, documentId);
                        else 
                            await QueryCollectionAsync(attr.TableName);
                        break;
                    case FirebaseOperation.Update:
                        await UpdateDocumentAsync(attr.TableName, attr.DocumentId, ParseDataString(attr.Data));
                        break;
                    case FirebaseOperation.Delete:
                        await DeleteDocumentAsync(attr.TableName, attr.DocumentId);
                        break;
                    case FirebaseOperation.Query:
                        if (attr.TableName == "auth") {
                            await HandleAuthenticationQuery(attr, instance);
                        } else {
                            var (field, value) = attr.Fields.Length > 1 
                                ? (attr.Fields[0], attr.Fields[1]) 
                                : (attr.Fields.FirstOrDefault() ?? "", attr.Data);
                            await QueryCollectionAsync(attr.TableName, field, value);
                        }
                        break;
                }
            } catch (Exception ex) {
                Debug.LogError($"[Firebase] Operation failed: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }
        }
        
        private string GetDynamicDocumentId(FirebaseMethodAttribute attr, object instance) {
            if (!string.IsNullOrEmpty(attr.DocumentId)) return attr.DocumentId;

            var contextMethod = instance.GetType().GetMethod("GetFirebaseContext");
            if (contextMethod != null) {
                var userId = contextMethod.Invoke(instance, new object[] { "userId" })?.ToString();
                if (!string.IsNullOrEmpty(userId)) return userId;
            }
            
            if (attr.TableName == "users" && CurrentUser != null) return CurrentUser.uid;
            
            return null;
        }
        
        private async Task HandleAuthenticationQuery(FirebaseMethodAttribute attr, object instance) {
            var emailProp = instance.GetType().GetProperty("UserEmail", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var passwordProp = instance.GetType().GetProperty("UserPassword", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (emailProp != null && passwordProp != null) {
                var email = emailProp.GetValue(instance)?.ToString();
                var password = passwordProp.GetValue(instance)?.ToString();
                
                if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password)) {
                    var signInResult = await SignInAsync(email, password);
                    if (signInResult.success) {
                        OnQueryCompleted?.Invoke(signInResult);
                    }
                } else {
                    var contextMethod = instance.GetType().GetMethod("GetFirebaseContext");
                    if (contextMethod != null) {
                        email = contextMethod.Invoke(instance, new object[] { "email" })?.ToString();
                        password = contextMethod.Invoke(instance, new object[] { "password" })?.ToString();
                        
                        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password)) {
                            var signInResult = await SignInAsync(email, password);
                            if (signInResult.success) {
                                OnQueryCompleted?.Invoke(signInResult);
                            }
                            return;
                        }
                    }
                    
                    var errorResult = new FirebaseQueryResult { error = "Email or password is empty" };
                    OnError?.Invoke("Email or password is empty");
                    OnQueryCompleted?.Invoke(errorResult);
                }
            } else {
                var errorResult = new FirebaseQueryResult { error = "Could not find email/password properties" };
                OnError?.Invoke("Could not find email/password properties");
                OnQueryCompleted?.Invoke(errorResult);
            }
        }

        private Dictionary<string, object> ParseDataString(string dataString) {
            if (string.IsNullOrEmpty(dataString)) return new Dictionary<string, object>();
            
            try {
                return JsonUtility.FromJson<Dictionary<string, object>>(dataString) ?? new Dictionary<string, object>();
            } catch {
                return new Dictionary<string, object> { ["data"] = dataString };
            }
        }

        private void OnDestroy() {
            if (auth != null) auth.StateChanged -= OnAuthStateChanged;
        }

        public void Dispose()  {
            if (auth != null) auth.StateChanged -= OnAuthStateChanged;

            app = null;
            auth = null;
            firestore = null;
        }
    }

    public abstract class FirebaseMonoBehaviour : MonoBehaviour {
        protected FirebaseQueryResult firebaseQueryResult => FirebaseManager.Instance?.LastQueryResult;
        protected virtual void Awake() => FirebaseTracker.Log(this);
        protected virtual void Start() => FirebaseTracker.Log(this);
        protected void LogFirebase([CallerMemberName] string methodName = "") => FirebaseTracker.Log(this, methodName);
    }
}
