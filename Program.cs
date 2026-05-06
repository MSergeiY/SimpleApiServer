using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using BCrypt.Net;
using System.IO;

public class AppConfig
{
    public List<string> ListenUrls { get; set; } = new();
    public string LogLevel { get; set; } = "Information";
    public string LogFilePath { get; set; } = "logs/app.log";
}

namespace SimpleApiServer
{
    public enum Priority
    {
        Low,
        Medium,
        High
    }

    public class TaskItem
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsCompleted { get; set; }
        public Priority Priority { get; set; } = Priority.Medium;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DueDate { get; set; }
    }

    public class CreateTaskRequest
    {
        public string Title { get; set; } = "";
        public string? Description { get; set; }
        public int? Priority { get; set; }
        public bool? IsCompleted { get; set; }
        public DateTime? DueDate { get; set; }
    }

    public class UpdateTaskRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Priority { get; set; }
        public bool? IsCompleted { get; set; }
        public DateTime? DueDate { get; set; }
    }

    public class ValidationError
    {
        public string Field { get; }
        public string Message { get; }
        public ValidationError(string field, string message) => (Field, Message) = (field, message);
    }

    public class User
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class RegisterRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    class Program
    {
        // --- JWT настройки ---
        private const string JwtSecretKey = "YourSuperSecretKeyForJwtTokenGenerationThatIsAtLeast32BytesLong!";
        private const string JwtIssuer = "SimpleApiServer";
        private const string JwtAudience = "SimpleApiClient";
        private static readonly SymmetricSecurityKey _securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecretKey));
        private static readonly SigningCredentials _signingCredentials = new SigningCredentials(_securityKey, SecurityAlgorithms.HmacSha256);

        // --- Хранилища ---
        static List<TaskItem> _tasks = new List<TaskItem>
        {
            new TaskItem
            {
                Id = 1,
                Title = "Сделать лабораторную",
                IsCompleted = false,
                Priority = Priority.High,
                CreatedAt = new DateTime(2026, 1, 1),
                DueDate = new DateTime(2027, 1, 1)
            },
            new TaskItem
            {
                Id = 2,
                Title = "Проверить почту",
                IsCompleted = true,
                Priority = Priority.Low,
                CreatedAt = new DateTime(2026, 3, 25)
            }
        };

        static List<User> _users = new List<User>();

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        // ==================================================================
        //  ЛОГГЕР (консоль + файл)
        // ==================================================================
        private static class Logger
        {
            private static string _logLevel = "Information";
            private static string? _logFilePath;
            private static readonly object _lock = new();

            public static void Initialize(string level, string? filePath)
            {
                _logLevel = level;
                _logFilePath = filePath;
                if (!string.IsNullOrEmpty(_logFilePath))
                {
                    var dir = Path.GetDirectoryName(_logFilePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }
            }

            private static bool ShouldLog(string level) =>
                (level == "Error") ||
                (level == "Warning" && (_logLevel == "Warning" || _logLevel == "Information")) ||
                (level == "Info" && _logLevel == "Information");

            private static void Write(string level, string message)
            {
                if (!ShouldLog(level)) return;
                var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
                Console.WriteLine(line);
                if (!string.IsNullOrEmpty(_logFilePath))
                {
                    lock (_lock)
                    {
                        File.AppendAllText(_logFilePath, line + Environment.NewLine);
                    }
                }
            }

            public static void Info(string msg) => Write("Info", msg);
            public static void Warning(string msg) => Write("Warning", msg);
            public static void Error(string msg, Exception? ex = null) =>
                Write("Error", ex == null ? msg : $"{msg}: {ex.Message}");
        }

        static void Main(string[] args)
        {
            // 1. Чтение конфигурации
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
            {
                Console.WriteLine("Ошибка: файл appsettings.json не найден.");
                return;
            }
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions)
                         ?? throw new InvalidOperationException("Не удалось загрузить конфигурацию.");

            // 2. Инициализация логгера
            Logger.Initialize(config.LogLevel, config.LogFilePath);
            Logger.Info("Сервер запускается...");

            // 3. Запуск HttpListener
            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add(config.ListenUrls[0]);
                listener.Start();

                Logger.Info($"Сервер запущен: {config.ListenUrls[0]}");
                Logger.Info("Endpoint-ы:");
                Logger.Info("  POST   /api/auth/register");
                Logger.Info("  POST   /api/auth/login");
                Logger.Info("  GET    /api/tasks");
                Logger.Info("  POST   /api/tasks (защищён JWT)");
                Logger.Info("  GET    /api/tasks/{id}");
                Logger.Info("  PUT    /api/tasks/{id}");
                Logger.Info("  DELETE /api/tasks/{id}");
                Logger.Info("Для остановки нажмите Ctrl+C");

                while (true)
                {
                    HttpListenerContext context = listener.GetContext();
                    HttpListenerRequest request = context.Request;
                    HttpListenerResponse response = context.Response;

                    try
                    {
                        Logger.Info($"Запрос: {request.HttpMethod} {request.RawUrl}");
                        RouteRequest(request, response);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Необработанное исключение в Main: {ex.GetType().Name}", ex);
                        WriteInternalServerError(response, "Произошла внутренняя ошибка сервера");
                    }
                }
            }
        }

        private static void RouteRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            string path = request.Url?.AbsolutePath ?? "";
            string method = request.HttpMethod;

            if (path == "/api/auth/register" && method == "POST")
            {
                HandleRegister(request, response);
                return;
            }
            if (path == "/api/auth/login" && method == "POST")
            {
                HandleLogin(request, response);
                return;
            }

            if (path.StartsWith("/api/tasks"))
            {
                string remaining = path.Substring("/api/tasks".Length).Trim('/');

                if (string.IsNullOrEmpty(remaining))
                {
                    switch (method)
                    {
                        case "GET":
                            HandleGetTasks(request, response);
                            return;
                        case "POST":
                            if (!ValidateJwtFromRequest(request, response))
                                return;
                            HandleCreateTask(request, response);
                            return;
                        default:
                            WriteError(response, 405, "Метод не поддерживается", "METHOD_NOT_ALLOWED");
                            return;
                    }
                }

                if (int.TryParse(remaining, out int id))
                {
                    switch (method)
                    {
                        case "GET":
                            HandleGetTaskById(request, response, id);
                            return;
                        case "PUT":
                            HandleUpdateTask(request, response, id);
                            return;
                        case "DELETE":
                            HandleDeleteTask(request, response, id);
                            return;
                        default:
                            WriteError(response, 405, "Метод не поддерживается", "METHOD_NOT_ALLOWED");
                            return;
                    }
                }

                Logger.Info($"404: ресурс {path} не найден");
                WriteError(response, 404, "Ресурс не найден", "NOT_FOUND");
                return;
            }

            Logger.Info($"404: ресурс {path} не найден");
            WriteError(response, 404, "Ресурс не найден", "NOT_FOUND");
        }

        // ==================================================================
        //  АУТЕНТИФИКАЦИЯ
        // ==================================================================

        private static void HandleRegister(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.ContentType == null || !request.ContentType.StartsWith("application/json"))
            {
                Logger.Warning("Регистрация: неверный Content-Type");
                WriteError(response, 400, "Требуется Content-Type: application/json", "INVALID_CONTENT_TYPE");
                return;
            }

            string body = ReadBody(request);
            if (string.IsNullOrWhiteSpace(body))
            {
                Logger.Warning("Регистрация: пустое тело запроса");
                WriteError(response, 400, "Пустое тело запроса", "EMPTY_BODY");
                return;
            }

            RegisterRequest? registerData;
            try
            {
                registerData = JsonSerializer.Deserialize<RegisterRequest>(body, _jsonOptions);
            }
            catch
            {
                Logger.Warning("Регистрация: некорректный JSON");
                WriteError(response, 400, "Некорректный JSON", "INVALID_JSON");
                return;
            }

            if (registerData == null || string.IsNullOrWhiteSpace(registerData.Email) || string.IsNullOrWhiteSpace(registerData.Password))
            {
                Logger.Warning("Регистрация: отсутствуют обязательные поля Email/Password");
                WriteError(response, 400, "Поля Email и Password обязательны", "MISSING_FIELDS");
                return;
            }

            lock (_users)
            {
                if (_users.Any(u => u.Email.Equals(registerData.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.Warning($"Регистрация: email {registerData.Email} уже существует");
                    WriteError(response, 400, "Пользователь с таким email уже существует", "EMAIL_EXISTS");
                    return;
                }

                string passwordHash = BCrypt.Net.BCrypt.HashPassword(registerData.Password);
                int newId = _users.Count == 0 ? 1 : _users.Max(u => u.Id) + 1;
                var newUser = new User
                {
                    Id = newId,
                    Email = registerData.Email,
                    PasswordHash = passwordHash,
                    Name = registerData.Name ?? ""
                };
                _users.Add(newUser);
                Logger.Info($"Зарегистрирован новый пользователь: {registerData.Email}");
            }

            WriteSuccess(response, new { message = "Регистрация успешна" }, 201);
        }

        private static void HandleLogin(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.ContentType == null || !request.ContentType.StartsWith("application/json"))
            {
                Logger.Warning("Логин: неверный Content-Type");
                WriteError(response, 400, "Требуется Content-Type: application/json", "INVALID_CONTENT_TYPE");
                return;
            }

            string body = ReadBody(request);
            if (string.IsNullOrWhiteSpace(body))
            {
                Logger.Warning("Логин: пустое тело запроса");
                WriteError(response, 400, "Пустое тело запроса", "EMPTY_BODY");
                return;
            }

            LoginRequest? loginData;
            try
            {
                loginData = JsonSerializer.Deserialize<LoginRequest>(body, _jsonOptions);
            }
            catch
            {
                Logger.Warning("Логин: некорректный JSON");
                WriteError(response, 400, "Некорректный JSON", "INVALID_JSON");
                return;
            }

            if (loginData == null || string.IsNullOrWhiteSpace(loginData.Email) || string.IsNullOrWhiteSpace(loginData.Password))
            {
                Logger.Warning("Логин: отсутствуют Email или Password");
                WriteError(response, 400, "Поля Email и Password обязательны", "MISSING_FIELDS");
                return;
            }

            User? user;
            lock (_users)
            {
                user = _users.FirstOrDefault(u => u.Email.Equals(loginData.Email, StringComparison.OrdinalIgnoreCase));
            }

            if (user == null || !BCrypt.Net.BCrypt.Verify(loginData.Password, user.PasswordHash))
            {
                Logger.Warning($"Неудачная попытка входа для {loginData.Email}");
                WriteError(response, 401, "Неверный email или пароль", "INVALID_CREDENTIALS");
                return;
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.Name)
            };

            var expires = DateTime.UtcNow.AddHours(1);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = expires,
                Issuer = JwtIssuer,
                Audience = JwtAudience,
                SigningCredentials = _signingCredentials
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.CreateToken(tokenDescriptor);
            string tokenString = tokenHandler.WriteToken(jwtToken);

            var result = new
            {
                token = tokenString,
                email = user.Email,
                expiresAt = expires.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };

            Logger.Info($"Пользователь {user.Email} успешно вошёл в систему");
            WriteSuccess(response, result);
        }

        // ==================================================================
        //  JWT
        // ==================================================================

        private static bool ValidateJwtFromRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            {
                Logger.Warning("Отсутствует или неверный заголовок Authorization");
                WriteUnauthorized(response, "Требуется авторизация");
                return false;
            }

            string token = authHeader.Substring("Bearer ".Length).Trim();
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = JwtAudience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = _securityKey,
                    ClockSkew = TimeSpan.Zero
                };

                tokenHandler.ValidateToken(token, validationParameters, out _);
                return true;
            }
            catch
            {
                Logger.Warning("Невалидный или просроченный JWT");
                WriteUnauthorized(response, "Невалидный или просроченный токен");
                return false;
            }
        }

        private static void WriteUnauthorized(HttpListenerResponse response, string message)
        {
            var errorResponse = new { error = "Unauthorized", message = message };
            string json = JsonSerializer.Serialize(errorResponse, _jsonOptions);
            WriteJson(response, json, 401);
        }

        // ==================================================================
        //  ОБРАБОТЧИКИ ЗАДАЧ
        // ==================================================================

        private static void HandleGetTasks(HttpListenerRequest request, HttpListenerResponse response)
        {
            lock (_tasks)
            {
                var tasks = _tasks.AsEnumerable();
                var query = request.Url.Query.TrimStart('?');
                var parameters = ParseQueryString(query);

                if (parameters.TryGetValue("isCompleted", out string isCompletedStr) && bool.TryParse(isCompletedStr, out bool isCompleted))
                    tasks = tasks.Where(t => t.IsCompleted == isCompleted);
                if (parameters.TryGetValue("priority", out string priorityStr) && !string.IsNullOrEmpty(priorityStr) && Enum.TryParse<Priority>(priorityStr, true, out Priority priority))
                    tasks = tasks.Where(t => t.Priority == priority);

                parameters.TryGetValue("orderBy", out string orderBy);
                parameters.TryGetValue("direction", out string direction);
                if (!string.IsNullOrEmpty(orderBy))
                {
                    orderBy = orderBy.ToLowerInvariant();
                    direction = direction?.ToLowerInvariant();
                    switch (orderBy)
                    {
                        case "title":
                            tasks = direction == "desc" ? tasks.OrderByDescending(t => t.Title) : tasks.OrderBy(t => t.Title);
                            break;
                        case "createdat":
                            tasks = direction == "desc" ? tasks.OrderByDescending(t => t.CreatedAt) : tasks.OrderBy(t => t.CreatedAt);
                            break;
                        case "priority":
                            tasks = direction == "desc" ? tasks.OrderByDescending(t => t.Priority) : tasks.OrderBy(t => t.Priority);
                            break;
                        default:
                            tasks = tasks.OrderBy(t => t.Id);
                            break;
                    }
                }
                else tasks = tasks.OrderBy(t => t.Id);

                int page = 1, pageSize = 10;
                if (parameters.TryGetValue("page", out string pageStr) && int.TryParse(pageStr, out int p) && p > 0) page = p;
                if (parameters.TryGetValue("pageSize", out string pageSizeStr) && int.TryParse(pageSizeStr, out int ps) && ps > 0) pageSize = ps;

                var resultList = tasks.Skip((page - 1) * pageSize).Take(pageSize).ToList();
                WriteSuccess(response, resultList);
            }
        }

        private static void HandleGetTaskById(HttpListenerRequest request, HttpListenerResponse response, int id)
        {
            lock (_tasks)
            {
                var task = _tasks.FirstOrDefault(t => t.Id == id);
                if (task == null)
                {
                    Logger.Info($"404: задача с id={id} не найдена");
                    WriteError(response, 404, "Задача не найдена", "NOT_FOUND");
                    return;
                }
                WriteSuccess(response, task);
            }
        }

        private static void HandleCreateTask(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.ContentType == null || !request.ContentType.StartsWith("application/json"))
            {
                Logger.Warning("Создание задачи: неверный Content-Type");
                WriteError(response, 400, "Требуется Content-Type: application/json", "INVALID_CONTENT_TYPE");
                return;
            }

            string body = ReadBody(request);
            if (string.IsNullOrWhiteSpace(body))
            {
                Logger.Warning("Создание задачи: пустое тело запроса");
                WriteError(response, 400, "Пустое тело запроса", "EMPTY_BODY");
                return;
            }

            CreateTaskRequest? createData;
            try
            {
                createData = JsonSerializer.Deserialize<CreateTaskRequest>(body, _jsonOptions);
            }
            catch
            {
                Logger.Warning("Создание задачи: некорректный JSON");
                WriteError(response, 400, "Некорректный JSON", "INVALID_JSON");
                return;
            }

            if (createData == null)
            {
                Logger.Warning("Создание задачи: невалидные данные");
                WriteError(response, 400, "Невалидные данные", "INVALID_DATA");
                return;
            }

            var errors = ValidateCreateRequest(createData);
            if (errors.Any())
            {
                Logger.Warning($"Ошибка валидации при создании задачи: {string.Join(", ", errors.Select(e => e.Field))}");
                WriteValidationError(response, errors);
                return;
            }

            var newTask = new TaskItem
            {
                Title = createData.Title,
                Description = createData.Description,
                IsCompleted = createData.IsCompleted ?? false,
                Priority = MapPriority(createData.Priority ?? 3),
                CreatedAt = DateTime.UtcNow,
                DueDate = createData.DueDate
            };

            lock (_tasks)
            {
                int newId = _tasks.Count == 0 ? 1 : _tasks.Max(t => t.Id) + 1;
                newTask.Id = newId;
                _tasks.Add(newTask);
                string location = $"{request.Url.GetLeftPart(UriPartial.Authority)}/api/tasks/{newId}";
                response.Headers.Set("Location", location);
                Logger.Info($"Создана задача #{newId}: {newTask.Title}");
                WriteSuccess(response, newTask, 201);
            }
        }

        private static void HandleUpdateTask(HttpListenerRequest request, HttpListenerResponse response, int id)
        {
            if (request.ContentType == null || !request.ContentType.StartsWith("application/json"))
            {
                Logger.Warning($"Обновление задачи #{id}: неверный Content-Type");
                WriteError(response, 400, "Требуется Content-Type: application/json", "INVALID_CONTENT_TYPE");
                return;
            }

            string body = ReadBody(request);
            if (string.IsNullOrWhiteSpace(body))
            {
                Logger.Warning($"Обновление задачи #{id}: пустое тело запроса");
                WriteError(response, 400, "Пустое тело запроса", "EMPTY_BODY");
                return;
            }

            UpdateTaskRequest? updateData;
            try
            {
                updateData = JsonSerializer.Deserialize<UpdateTaskRequest>(body, _jsonOptions);
            }
            catch
            {
                Logger.Warning($"Обновление задачи #{id}: некорректный JSON");
                WriteError(response, 400, "Некорректный JSON", "INVALID_JSON");
                return;
            }

            if (updateData == null)
            {
                Logger.Warning($"Обновление задачи #{id}: невалидные данные");
                WriteError(response, 400, "Невалидные данные", "INVALID_DATA");
                return;
            }

            var errors = ValidateUpdateRequest(updateData);
            if (errors.Any())
            {
                Logger.Warning($"Ошибка валидации при обновлении задачи #{id}: {string.Join(", ", errors.Select(e => e.Field))}");
                WriteValidationError(response, errors);
                return;
            }

            lock (_tasks)
            {
                var existing = _tasks.FirstOrDefault(t => t.Id == id);
                if (existing == null)
                {
                    Logger.Info($"404: попытка обновить несуществующую задачу #{id}");
                    WriteError(response, 404, "Задача не найдена", "NOT_FOUND");
                    return;
                }
                if (updateData.Title != null) existing.Title = updateData.Title;
                if (updateData.Description != null) existing.Description = updateData.Description;
                if (updateData.IsCompleted.HasValue) existing.IsCompleted = updateData.IsCompleted.Value;
                if (updateData.Priority.HasValue) existing.Priority = MapPriority(updateData.Priority.Value);
                if (updateData.DueDate.HasValue) existing.DueDate = updateData.DueDate;
                Logger.Info($"Обновлена задача #{id}");
                WriteSuccess(response, existing);
            }
        }

        private static void HandleDeleteTask(HttpListenerRequest request, HttpListenerResponse response, int id)
        {
            lock (_tasks)
            {
                var task = _tasks.FirstOrDefault(t => t.Id == id);
                if (task == null)
                {
                    Logger.Info($"404: попытка удалить несуществующую задачу #{id}");
                    WriteError(response, 404, "Задача не найдена", "NOT_FOUND");
                    return;
                }
                _tasks.Remove(task);
                Logger.Info($"Удалена задача #{id}");
                WriteSuccess(response, null, 200);
            }
        }

        // ==================================================================
        //  ВАЛИДАЦИЯ
        // ==================================================================

        private static List<ValidationError> ValidateCreateRequest(CreateTaskRequest data)
        {
            var errors = new List<ValidationError>();
            if (string.IsNullOrWhiteSpace(data.Title))
                errors.Add(new ValidationError("Title", "Название обязательно"));
            else if (data.Title.Length > 200)
                errors.Add(new ValidationError("Title", "Название не более 200 символов"));
            if (!string.IsNullOrEmpty(data.Description) && data.Description.Length > 1000)
                errors.Add(new ValidationError("Description", "Описание не более 1000 символов"));
            if (data.Priority.HasValue && (data.Priority < 1 || data.Priority > 5))
                errors.Add(new ValidationError("Priority", "Приоритет должен быть от 1 до 5"));
            return errors;
        }

        private static List<ValidationError> ValidateUpdateRequest(UpdateTaskRequest data)
        {
            var errors = new List<ValidationError>();
            if (data.Title != null)
            {
                if (string.IsNullOrWhiteSpace(data.Title))
                    errors.Add(new ValidationError("Title", "Название не может быть пустым"));
                else if (data.Title.Length > 200)
                    errors.Add(new ValidationError("Title", "Название не более 200 символов"));
            }
            if (data.Description != null && data.Description.Length > 1000)
                errors.Add(new ValidationError("Description", "Описание не более 1000 символов"));
            if (data.Priority.HasValue && (data.Priority < 1 || data.Priority > 5))
                errors.Add(new ValidationError("Priority", "Приоритет должен быть от 1 до 5"));
            return errors;
        }

        private static Priority MapPriority(int priority)
        {
            if (priority <= 2) return Priority.Low;
            if (priority <= 4) return Priority.Medium;
            return Priority.High;
        }

        // ==================================================================
        //  ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // ==================================================================

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJson(HttpListenerResponse response, string json, int statusCode)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.Close();
        }

        private static void WriteSuccess(HttpListenerResponse response, object? data, int statusCode = 200)
        {
            var result = new { data = data, error = (object?)null };
            string json = JsonSerializer.Serialize(result, _jsonOptions);
            WriteJson(response, json, statusCode);
        }

        private static void WriteError(HttpListenerResponse response, int statusCode, string message, string code)
        {
            var result = new { data = (object?)null, error = new { message, code } };
            string json = JsonSerializer.Serialize(result, _jsonOptions);
            WriteJson(response, json, statusCode);
        }

        private static void WriteValidationError(HttpListenerResponse response, List<ValidationError> errors)
        {
            var errorResponse = new
            {
                error = "Ошибка валидации",
                errors = errors.Select(e => new { e.Field, e.Message })
            };
            string json = JsonSerializer.Serialize(errorResponse, _jsonOptions);
            WriteJson(response, json, 400);
        }

        private static void WriteInternalServerError(HttpListenerResponse response, string message)
        {
            Logger.Error($"500 Internal Server Error: {message}");
            var errorResponse = new { error = "InternalServerError", message = message };
            string json = JsonSerializer.Serialize(errorResponse, _jsonOptions);
            WriteJson(response, json, 500);
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var pair in query.Split('&'))
            {
                var parts = pair.Split('=');
                if (parts.Length == 2)
                {
                    string key = Uri.UnescapeDataString(parts[0]);
                    string value = Uri.UnescapeDataString(parts[1]);
                    dict[key] = value;
                }
            }
            return dict;
        }
    }
}