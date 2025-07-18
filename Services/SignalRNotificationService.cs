using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using dotnet_rest_api.Data;
using dotnet_rest_api.DTOs;
using dotnet_rest_api.Hubs;
using dotnet_rest_api.Models;
using TaskModel = dotnet_rest_api.Models.Task;

namespace dotnet_rest_api.Services;

/// <summary>
/// Real-time notification service using SignalR for live updates
/// Handles work request notifications, daily report updates, and project status changes
/// </summary>
public class SignalRNotificationService : INotificationService
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<SignalRNotificationService> _logger;

    public SignalRNotificationService(
        IHubContext<NotificationHub> hubContext,
        ApplicationDbContext context,
        ILogger<SignalRNotificationService> logger)
    {
        _hubContext = hubContext;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Send a simple notification to a specific user
    /// </summary>
    public async System.Threading.Tasks.Task SendNotificationAsync(string message, Guid userId)
    {
        try
        {
            await _hubContext.Clients.Group($"user_{userId}")
                .SendAsync("ReceiveNotification", new
                {
                    Id = Guid.NewGuid(),
                    Message = message,
                    Type = "Info",
                    Timestamp = DateTime.UtcNow,
                    UserId = userId
                });

            _logger.LogInformation("Sent notification to user {UserId}: {Message}", userId, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification to user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// Send work request notification with full context
    /// </summary>
    public async System.Threading.Tasks.Task SendWorkRequestNotificationAsync(Guid workRequestId, NotificationType type, Guid recipientId, string message, Guid? senderId = null)
    {
        try
        {
            // Create notification record in database
            var notification = new WorkRequestNotification
            {
                NotificationId = Guid.NewGuid(),
                WorkRequestId = workRequestId,
                RecipientId = recipientId,
                SenderId = senderId,
                Type = type,
                Status = NotificationStatus.Pending,
                Subject = GetNotificationSubject(type),
                Message = message,
                CreatedAt = DateTime.UtcNow
            };

            _context.WorkRequestNotifications.Add(notification);
            await _context.SaveChangesAsync();

            // Get work request details for rich notification
            var workRequest = await _context.WorkRequests
                .Include(wr => wr.Project)
                .Include(wr => wr.RequestedBy)
                .FirstOrDefaultAsync(wr => wr.WorkRequestId == workRequestId);

            if (workRequest != null)
            {
                var notificationDto = new NotificationDto
                {
                    NotificationId = notification.NotificationId,
                    WorkRequestId = workRequestId,
                    WorkRequestTitle = workRequest.Title,
                    Type = type.ToString(),
                    Status = NotificationStatus.Sent.ToString(),
                    Subject = notification.Subject,
                    Message = message,
                    CreatedAt = notification.CreatedAt
                };

                // Send to specific user
                await _hubContext.Clients.Group($"user_{recipientId}")
                    .SendAsync("WorkRequestNotification", notificationDto);

                // Send to project group if relevant
                await _hubContext.Clients.Group($"project_{workRequest.ProjectId}")
                    .SendAsync("ProjectWorkRequestUpdate", new
                    {
                        ProjectId = workRequest.ProjectId,
                        WorkRequestId = workRequestId,
                        Type = type.ToString(),
                        Title = workRequest.Title,
                        Status = workRequest.Status.ToString(),
                        UpdatedAt = DateTime.UtcNow
                    });

                // Update notification status
                notification.Status = NotificationStatus.Sent;
                notification.SentAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation("Sent work request notification {Type} for request {WorkRequestId} to user {RecipientId}", 
                    type, workRequestId, recipientId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send work request notification for {WorkRequestId}", workRequestId);
            throw;
        }
    }

    private static string GetNotificationSubject(NotificationType type)
    {
        return type switch
        {
            NotificationType.WorkRequestSubmitted => "Work Request Submitted",
            NotificationType.WorkRequestApproved => "Work Request Approved",
            NotificationType.WorkRequestRejected => "Work Request Rejected", 
            NotificationType.WorkRequestAssigned => "Work Request Assigned",
            NotificationType.WorkRequestCompleted => "Work Request Completed",
            NotificationType.WorkRequestEscalated => "Work Request Escalated",
            NotificationType.WorkRequestDue => "Work Request Due",
            NotificationType.WorkRequestOverdue => "Work Request Overdue",
            NotificationType.ApprovalRequired => "Approval Required",
            NotificationType.ApprovalReminder => "Approval Reminder",
            _ => "Work Request Notification"
        };
    }

    /// <summary>
    /// Send daily report update notification
    /// </summary>
    public async System.Threading.Tasks.Task SendDailyReportNotificationAsync(Guid dailyReportId, string type, Guid projectId, string message)
    {
        try
        {
            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("DailyReportUpdate", new
                {
                    DailyReportId = dailyReportId,
                    ProjectId = projectId,
                    Type = type,
                    Message = message,
                    Timestamp = DateTime.UtcNow
                });

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("DailyReportManagerUpdate", new
                {
                    DailyReportId = dailyReportId,
                    ProjectId = projectId,
                    Type = type,
                    Message = message,
                    Timestamp = DateTime.UtcNow
                });

            _logger.LogInformation("Sent daily report notification for report {DailyReportId}, type {Type}", dailyReportId, type);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily report notification for {DailyReportId}", dailyReportId);
            throw;
        }
    }

    // Enhanced Daily Report Notifications
    public async System.Threading.Tasks.Task SendDailyReportCreatedNotificationAsync(Guid dailyReportId, Guid projectId, string reporterName)
    {
        try
        {
            var notificationData = new
            {
                DailyReportId = dailyReportId,
                ProjectId = projectId,
                Type = "DailyReportCreated",
                Message = $"New daily report submitted by {reporterName}",
                ReporterName = reporterName,
                Timestamp = DateTime.UtcNow
            };

            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("DailyReportCreated", notificationData);

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("NewDailyReportAlert", notificationData);

            _logger.LogInformation("Sent daily report created notification for report {DailyReportId}", dailyReportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily report created notification for {DailyReportId}", dailyReportId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendDailyReportApprovalStatusChangeAsync(Guid dailyReportId, Guid projectId, string newStatus, string approverName)
    {
        try
        {
            var notificationData = new
            {
                DailyReportId = dailyReportId,
                ProjectId = projectId,
                Type = "DailyReportStatusChange",
                Status = newStatus,
                ApproverName = approverName,
                Message = $"Daily report status changed to {newStatus} by {approverName}",
                Timestamp = DateTime.UtcNow
            };

            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("DailyReportStatusChanged", notificationData);

            // Send to report session if anyone is editing
            await _hubContext.Clients.Group($"report_session_{dailyReportId}")
                .SendAsync("ReportStatusUpdated", notificationData);

            _logger.LogInformation("Sent daily report status change notification for report {DailyReportId}, new status: {Status}", 
                dailyReportId, newStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily report status change notification for {DailyReportId}", dailyReportId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendRealTimeProgressUpdateAsync(Guid projectId, decimal progressPercentage, string milestone = "")
    {
        try
        {
            var updateData = new
            {
                ProjectId = projectId,
                ProgressPercentage = progressPercentage,
                Milestone = milestone,
                Type = "ProgressUpdate",
                Timestamp = DateTime.UtcNow
            };

            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("ProjectProgressUpdate", updateData);

            // Send to dashboards and management views
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("DashboardProgressUpdate", updateData);

            _logger.LogInformation("Sent real-time progress update for project {ProjectId}: {Progress}%", 
                projectId, progressPercentage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send progress update for project {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send project status update notification
    /// </summary>
    public async System.Threading.Tasks.Task SendProjectStatusUpdateAsync(Guid projectId, string statusUpdate, decimal? completionPercentage = null)
    {
        try
        {
            var updateData = new
            {
                ProjectId = projectId,
                StatusUpdate = statusUpdate,
                CompletionPercentage = completionPercentage,
                Timestamp = DateTime.UtcNow
            };

            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("ProjectStatusUpdate", updateData);

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("ProjectManagerUpdate", updateData);

            _logger.LogInformation("Sent project status update for project {ProjectId}: {StatusUpdate}", projectId, statusUpdate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project status update for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send approval required notification to managers/admins
    /// </summary>
    public async System.Threading.Tasks.Task SendApprovalRequiredNotificationAsync(Guid workRequestId, string title, decimal? estimatedCost)
    {
        try
        {
            var notificationData = new
            {
                WorkRequestId = workRequestId,
                Title = title,
                EstimatedCost = estimatedCost,
                Type = "ApprovalRequired",
                Timestamp = DateTime.UtcNow,
                Message = $"Work request '{title}' requires approval"
            };

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("ApprovalRequired", notificationData);

            _logger.LogInformation("Sent approval required notification for work request {WorkRequestId}", workRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send approval required notification for {WorkRequestId}", workRequestId);
            throw;
        }
    }

    /// <summary>
    /// Send notification count update to a specific user
    /// </summary>
    public async System.Threading.Tasks.Task SendNotificationCountUpdateAsync(Guid userId)
    {
        try
        {
            var unreadCount = await _context.WorkRequestNotifications
                .Where(n => n.RecipientId == userId && n.ReadAt == null)
                .CountAsync();

            await _hubContext.Clients.Group($"user_{userId}")
                .SendAsync("NotificationCountUpdated", unreadCount);

            _logger.LogInformation("Sent notification count update to user {UserId}: {Count}", userId, unreadCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification count update to user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// Broadcast system-wide announcement
    /// </summary>
    public async System.Threading.Tasks.Task SendSystemAnnouncementAsync(string title, string message, string priority = "Info")
    {
        try
        {
            var announcement = new
            {
                Title = title,
                Message = message,
                Priority = priority,
                Timestamp = DateTime.UtcNow,
                Type = "SystemAnnouncement"
            };

            await _hubContext.Clients.All.SendAsync("SystemAnnouncement", announcement);

            _logger.LogInformation("Sent system announcement: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send system announcement: {Title}", title);
            throw;
        }
    }

    /// <summary>
    /// Mark notification as read
    /// </summary>
    public async System.Threading.Tasks.Task MarkNotificationAsReadAsync(Guid notificationId, Guid userId)
    {
        try
        {
            var notification = await _context.WorkRequestNotifications
                .FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.RecipientId == userId);

            if (notification != null)
            {
                notification.ReadAt = DateTime.UtcNow;
                notification.Status = NotificationStatus.Read;
                await _context.SaveChangesAsync();

                // Send updated count
                await SendNotificationCountUpdateAsync(userId);

                _logger.LogInformation("Marked notification {NotificationId} as read for user {UserId}", notificationId, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark notification {NotificationId} as read for user {UserId}", notificationId, userId);
            throw;
        }
    }

    #region Work Request Notifications

    public async System.Threading.Tasks.Task SendWorkRequestCreatedNotificationAsync(Guid workRequestId, string title, Guid projectId)
    {
        try
        {
            var notificationData = new
            {
                WorkRequestId = workRequestId,
                Title = title,
                ProjectId = projectId,
                Type = "WorkRequestCreated",
                Message = $"New work request created: {title}",
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WorkRequestCreated", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("WorkRequestCreated", notificationData);

            _logger.LogInformation("Sent work request created notification for {WorkRequestId}", workRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send work request created notification for {WorkRequestId}", workRequestId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWorkRequestUpdatedNotificationAsync(Guid workRequestId, string title, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                WorkRequestId = workRequestId,
                Title = title,
                ProjectId = projectId,
                Type = "WorkRequestUpdated",
                Message = $"Work request updated by {updatedBy}: {title}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WorkRequestUpdated", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("WorkRequestUpdated", notificationData);

            _logger.LogInformation("Sent work request updated notification for {WorkRequestId}", workRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send work request updated notification for {WorkRequestId}", workRequestId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWorkRequestDeletedNotificationAsync(string title, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                Title = title,
                ProjectId = projectId,
                Type = "WorkRequestDeleted",
                Message = $"Work request deleted by {deletedBy}: {title}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WorkRequestDeleted", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("WorkRequestDeleted", notificationData);

            _logger.LogInformation("Sent work request deleted notification for {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send work request deleted notification for {Title}", title);
            throw;
        }
    }

    #endregion

    #region Daily Report Notifications

    public async System.Threading.Tasks.Task SendDailyReportUpdatedNotificationAsync(Guid dailyReportId, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                DailyReportId = dailyReportId,
                ProjectId = projectId,
                Type = "DailyReportUpdated",
                Message = $"Daily report updated by {updatedBy}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("DailyReportUpdated", notificationData);
            await _hubContext.Clients.Group($"report_session_{dailyReportId}").SendAsync("ReportUpdated", notificationData);

            _logger.LogInformation("Sent daily report updated notification for {DailyReportId}", dailyReportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily report updated notification for {DailyReportId}", dailyReportId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendDailyReportDeletedNotificationAsync(Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                ProjectId = projectId,
                Type = "DailyReportDeleted",
                Message = $"Daily report deleted by {deletedBy}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("DailyReportDeleted", notificationData);

            _logger.LogInformation("Sent daily report deleted notification for project {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send daily report deleted notification for project {ProjectId}", projectId);
            throw;
        }
    }

    #endregion

    #region Project Notifications

    public async System.Threading.Tasks.Task SendProjectCreatedNotificationAsync(Guid projectId, string projectName, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                Type = "ProjectCreated",
                Message = $"New project created by {createdBy}: {projectName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.All.SendAsync("ProjectCreated", notificationData);

            _logger.LogInformation("Sent project created notification for {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project created notification for {ProjectId}", projectId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendProjectUpdatedNotificationAsync(Guid projectId, string projectName, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                Type = "ProjectUpdated",
                Message = $"Project updated by {updatedBy}: {projectName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("ProjectUpdated", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("ProjectUpdated", notificationData);

            _logger.LogInformation("Sent project updated notification for {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project updated notification for {ProjectId}", projectId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendProjectDeletedNotificationAsync(string projectName, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                ProjectName = projectName,
                Type = "ProjectDeleted",
                Message = $"Project deleted by {deletedBy}: {projectName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.All.SendAsync("ProjectDeleted", notificationData);

            _logger.LogInformation("Sent project deleted notification for {ProjectName}", projectName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project deleted notification for {ProjectName}", projectName);
            throw;
        }
    }

    #endregion

    #region Task Notifications

    public async System.Threading.Tasks.Task SendTaskCreatedNotificationAsync(Guid taskId, string taskName, Guid projectId, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                TaskId = taskId,
                TaskName = taskName,
                ProjectId = projectId,
                Type = "TaskCreated",
                Message = $"New task created by {createdBy}: {taskName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("TaskCreated", notificationData);

            _logger.LogInformation("Sent task created notification for {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send task created notification for {TaskId}", taskId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendTaskUpdatedNotificationAsync(Guid taskId, string taskName, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                TaskId = taskId,
                TaskName = taskName,
                ProjectId = projectId,
                Type = "TaskUpdated",
                Message = $"Task updated by {updatedBy}: {taskName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("TaskUpdated", notificationData);

            _logger.LogInformation("Sent task updated notification for {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send task updated notification for {TaskId}", taskId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendTaskDeletedNotificationAsync(string taskName, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                TaskName = taskName,
                ProjectId = projectId,
                Type = "TaskDeleted",
                Message = $"Task deleted by {deletedBy}: {taskName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("TaskDeleted", notificationData);

            _logger.LogInformation("Sent task deleted notification for {TaskName}", taskName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send task deleted notification for {TaskName}", taskName);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendTaskStatusChangedNotificationAsync(Guid taskId, string taskName, Guid projectId, string newStatus, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                TaskId = taskId,
                TaskName = taskName,
                ProjectId = projectId,
                NewStatus = newStatus,
                Type = "TaskStatusChanged",
                Message = $"Task status changed to {newStatus} by {updatedBy}: {taskName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("TaskStatusChanged", notificationData);

            _logger.LogInformation("Sent task status changed notification for {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send task status changed notification for {TaskId}", taskId);
            throw;
        }
    }

    #endregion

    #region User Notifications

    public async System.Threading.Tasks.Task SendUserCreatedNotificationAsync(Guid userId, string userName, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                UserId = userId,
                UserName = userName,
                Type = "UserCreated",
                Message = $"New user created by {createdBy}: {userName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("UserCreated", notificationData);

            _logger.LogInformation("Sent user created notification for {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send user created notification for {UserId}", userId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendUserUpdatedNotificationAsync(Guid userId, string userName, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                UserId = userId,
                UserName = userName,
                Type = "UserUpdated",
                Message = $"User updated by {updatedBy}: {userName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"user_{userId}").SendAsync("UserProfileUpdated", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("UserUpdated", notificationData);

            _logger.LogInformation("Sent user updated notification for {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send user updated notification for {UserId}", userId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendUserDeletedNotificationAsync(string userName, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                UserName = userName,
                Type = "UserDeleted",
                Message = $"User deleted by {deletedBy}: {userName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("UserDeleted", notificationData);

            _logger.LogInformation("Sent user deleted notification for {UserName}", userName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send user deleted notification for {UserName}", userName);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendUserRoleChangedNotificationAsync(Guid userId, string userName, string newRole, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                UserId = userId,
                UserName = userName,
                NewRole = newRole,
                Type = "UserRoleChanged",
                Message = $"User role changed to {newRole} by {updatedBy}: {userName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"user_{userId}").SendAsync("UserRoleChanged", notificationData);
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("UserRoleChanged", notificationData);

            _logger.LogInformation("Sent user role changed notification for {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send user role changed notification for {UserId}", userId);
            throw;
        }
    }

    #endregion

    #region Calendar Event Notifications

    public async System.Threading.Tasks.Task SendCalendarEventCreatedNotificationAsync(Guid eventId, string eventTitle, Guid projectId, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                EventId = eventId,
                EventTitle = eventTitle,
                ProjectId = projectId,
                Type = "CalendarEventCreated",
                Message = $"New calendar event created by {createdBy}: {eventTitle}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("CalendarEventCreated", notificationData);

            _logger.LogInformation("Sent calendar event created notification for {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send calendar event created notification for {EventId}", eventId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendCalendarEventUpdatedNotificationAsync(Guid eventId, string eventTitle, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                EventId = eventId,
                EventTitle = eventTitle,
                ProjectId = projectId,
                Type = "CalendarEventUpdated",
                Message = $"Calendar event updated by {updatedBy}: {eventTitle}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("CalendarEventUpdated", notificationData);

            _logger.LogInformation("Sent calendar event updated notification for {EventId}", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send calendar event updated notification for {EventId}", eventId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendCalendarEventDeletedNotificationAsync(string eventTitle, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                EventTitle = eventTitle,
                ProjectId = projectId,
                Type = "CalendarEventDeleted",
                Message = $"Calendar event deleted by {deletedBy}: {eventTitle}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("CalendarEventDeleted", notificationData);

            _logger.LogInformation("Sent calendar event deleted notification for {EventTitle}", eventTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send calendar event deleted notification for {EventTitle}", eventTitle);
            throw;
        }
    }

    #endregion

    #region Master Plan Notifications

    public async System.Threading.Tasks.Task SendMasterPlanCreatedNotificationAsync(Guid masterPlanId, string planName, Guid projectId, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                MasterPlanId = masterPlanId,
                PlanName = planName,
                ProjectId = projectId,
                Type = "MasterPlanCreated",
                Message = $"New master plan created by {createdBy}: {planName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("MasterPlanCreated", notificationData);

            _logger.LogInformation("Sent master plan created notification for {MasterPlanId}", masterPlanId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send master plan created notification for {MasterPlanId}", masterPlanId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendMasterPlanUpdatedNotificationAsync(Guid masterPlanId, string planName, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                MasterPlanId = masterPlanId,
                PlanName = planName,
                ProjectId = projectId,
                Type = "MasterPlanUpdated",
                Message = $"Master plan updated by {updatedBy}: {planName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("MasterPlanUpdated", notificationData);

            _logger.LogInformation("Sent master plan updated notification for {MasterPlanId}", masterPlanId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send master plan updated notification for {MasterPlanId}", masterPlanId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendMasterPlanDeletedNotificationAsync(string planName, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                PlanName = planName,
                ProjectId = projectId,
                Type = "MasterPlanDeleted",
                Message = $"Master plan deleted by {deletedBy}: {planName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("MasterPlanDeleted", notificationData);

            _logger.LogInformation("Sent master plan deleted notification for {PlanName}", planName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send master plan deleted notification for {PlanName}", planName);
            throw;
        }
    }

    #endregion

    #region WBS Notifications

    public async System.Threading.Tasks.Task SendWbsTaskCreatedNotificationAsync(Guid taskId, string wbsId, string taskName, Guid projectId, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                TaskId = taskId,
                WbsId = wbsId,
                TaskName = taskName,
                ProjectId = projectId,
                Type = "WbsTaskCreated",
                Message = $"New WBS task created by {createdBy}: {wbsId} - {taskName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WbsTaskCreated", notificationData);

            _logger.LogInformation("Sent WBS task created notification for {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WBS task created notification for {TaskId}", taskId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWbsTaskUpdatedNotificationAsync(Guid taskId, string wbsId, string taskName, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                TaskId = taskId,
                WbsId = wbsId,
                TaskName = taskName,
                ProjectId = projectId,
                Type = "WbsTaskUpdated",
                Message = $"WBS task updated by {updatedBy}: {wbsId} - {taskName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WbsTaskUpdated", notificationData);

            _logger.LogInformation("Sent WBS task updated notification for {TaskId}", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WBS task updated notification for {TaskId}", taskId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWbsTaskDeletedNotificationAsync(string wbsId, string taskName, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                WbsId = wbsId,
                TaskName = taskName,
                ProjectId = projectId,
                Type = "WbsTaskDeleted",
                Message = $"WBS task deleted by {deletedBy}: {wbsId} - {taskName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WbsTaskDeleted", notificationData);

            _logger.LogInformation("Sent WBS task deleted notification for {WbsId}", wbsId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send WBS task deleted notification for {WbsId}", wbsId);
            throw;
        }
    }

    #endregion

    #region Resource Notifications

    public async System.Threading.Tasks.Task SendResourceCreatedNotificationAsync(Guid resourceId, string resourceName, string createdBy)
    {
        try
        {
            var notificationData = new
            {
                ResourceId = resourceId,
                ResourceName = resourceName,
                Type = "ResourceCreated",
                Message = $"New resource created by {createdBy}: {resourceName}",
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("ResourceCreated", notificationData);

            _logger.LogInformation("Sent resource created notification for {ResourceId}", resourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send resource created notification for {ResourceId}", resourceId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendResourceUpdatedNotificationAsync(Guid resourceId, string resourceName, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                ResourceId = resourceId,
                ResourceName = resourceName,
                Type = "ResourceUpdated",
                Message = $"Resource updated by {updatedBy}: {resourceName}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("ResourceUpdated", notificationData);

            _logger.LogInformation("Sent resource updated notification for {ResourceId}", resourceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send resource updated notification for {ResourceId}", resourceId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendResourceDeletedNotificationAsync(string resourceName, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                ResourceName = resourceName,
                Type = "ResourceDeleted",
                Message = $"Resource deleted by {deletedBy}: {resourceName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" }).SendAsync("ResourceDeleted", notificationData);

            _logger.LogInformation("Sent resource deleted notification for {ResourceName}", resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send resource deleted notification for {ResourceName}", resourceName);
            throw;
        }
    }

    #endregion

    #region Document Notifications

    public async System.Threading.Tasks.Task SendDocumentUploadedNotificationAsync(Guid documentId, string fileName, Guid projectId, string uploadedBy)
    {
        try
        {
            var notificationData = new
            {
                DocumentId = documentId,
                FileName = fileName,
                ProjectId = projectId,
                Type = "DocumentUploaded",
                Message = $"Document uploaded by {uploadedBy}: {fileName}",
                UploadedBy = uploadedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("DocumentUploaded", notificationData);

            _logger.LogInformation("Sent document uploaded notification for {DocumentId}", documentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send document uploaded notification for {DocumentId}", documentId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendDocumentDeletedNotificationAsync(string fileName, Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                FileName = fileName,
                ProjectId = projectId,
                Type = "DocumentDeleted",
                Message = $"Document deleted by {deletedBy}: {fileName}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("DocumentDeleted", notificationData);

            _logger.LogInformation("Sent document deleted notification for {FileName}", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send document deleted notification for {FileName}", fileName);
            throw;
        }
    }

    #endregion

    #region Weekly Report Notifications

    public async System.Threading.Tasks.Task SendWeeklyReportCreatedNotificationAsync(Guid reportId, Guid projectId, string reporterName)
    {
        try
        {
            var notificationData = new
            {
                ReportId = reportId,
                ProjectId = projectId,
                Type = "WeeklyReportCreated",
                Message = $"New weekly report created by {reporterName}",
                ReporterName = reporterName,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WeeklyReportCreated", notificationData);

            _logger.LogInformation("Sent weekly report created notification for {ReportId}", reportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send weekly report created notification for {ReportId}", reportId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWeeklyReportUpdatedNotificationAsync(Guid reportId, Guid projectId, string updatedBy)
    {
        try
        {
            var notificationData = new
            {
                ReportId = reportId,
                ProjectId = projectId,
                Type = "WeeklyReportUpdated",
                Message = $"Weekly report updated by {updatedBy}",
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WeeklyReportUpdated", notificationData);

            _logger.LogInformation("Sent weekly report updated notification for {ReportId}", reportId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send weekly report updated notification for {ReportId}", reportId);
            throw;
        }
    }

    public async System.Threading.Tasks.Task SendWeeklyReportDeletedNotificationAsync(Guid projectId, string deletedBy)
    {
        try
        {
            var notificationData = new
            {
                ProjectId = projectId,
                Type = "WeeklyReportDeleted",
                Message = $"Weekly report deleted by {deletedBy}",
                DeletedBy = deletedBy,
                Timestamp = DateTime.UtcNow
            };

            await _hubContext.Clients.Group($"project_{projectId}").SendAsync("WeeklyReportDeleted", notificationData);

            _logger.LogInformation("Sent weekly report deleted notification for project {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send weekly report deleted notification for project {ProjectId}", projectId);
            throw;
        }
    }

    #endregion

    #region Enhanced Real-Time Project Features (July 2025)

    /// <summary>
    /// Send comprehensive project status change notification with timeline tracking
    /// </summary>
    public async System.Threading.Tasks.Task SendProjectStatusChangedNotificationAsync(
        Guid projectId, 
        string projectName, 
        ProjectStatus oldStatus, 
        ProjectStatus newStatus, 
        DateTime? actualEndDate = null,
        decimal? completionPercentage = null,
        string? updatedBy = null)
    {
        try
        {
            var statusChangeData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                OldStatus = oldStatus.ToString(),
                NewStatus = newStatus.ToString(),
                ActualEndDate = actualEndDate,
                CompletionPercentage = completionPercentage,
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow,
                Type = "ProjectStatusChanged"
            };

            // Send to project-specific group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("ProjectStatusChanged", statusChangeData);

            // Send to managers and admins for oversight
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("ProjectStatusChanged", statusChangeData);

            // Send to all users for dashboard updates
            await _hubContext.Clients.All
                .SendAsync("EntityUpdated", new
                {
                    EntityType = "Project",
                    EntityId = projectId,
                    ChangeType = "StatusChanged",
                    Data = statusChangeData
                });

            _logger.LogInformation("Sent project status change notification for {ProjectId}: {OldStatus} -> {NewStatus}", 
                projectId, oldStatus, newStatus);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project status change notification for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send real-time location and address update notification
    /// </summary>
    public async System.Threading.Tasks.Task SendProjectLocationUpdatedNotificationAsync(
        Guid projectId,
        string projectName,
        string? address = null,
        decimal? latitude = null,
        decimal? longitude = null,
        string? updatedBy = null)
    {
        try
        {
            var locationData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                Address = address,
                Coordinates = new
                {
                    Latitude = latitude,
                    Longitude = longitude
                },
                UpdatedBy = updatedBy,
                Timestamp = DateTime.UtcNow,
                Type = "LocationUpdated"
            };

            // Send to project-specific group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("LocationUpdated", locationData);

            // Send to geographic region groups if coordinates are provided
            if (latitude.HasValue && longitude.HasValue)
            {
                var region = DetermineThailandRegion(latitude.Value, longitude.Value);
                await _hubContext.Clients.Group($"region_{region}")
                    .SendAsync("RegionalProjectUpdate", new
                    {
                        Region = region,
                        ProjectId = projectId,
                        ProjectName = projectName,
                        LocationData = locationData
                    });
            }

            // Send to map viewers
            await _hubContext.Clients.Group("map_viewers")
                .SendAsync("ProjectLocationUpdated", locationData);

            // Send universal entity update
            await _hubContext.Clients.All
                .SendAsync("EntityUpdated", new
                {
                    EntityType = "Project",
                    EntityId = projectId,
                    ChangeType = "LocationUpdated",
                    Data = locationData
                });

            _logger.LogInformation("Sent project location update notification for {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send project location update notification for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send enhanced project creation notification with location and status data
    /// </summary>
    public async System.Threading.Tasks.Task SendEnhancedProjectCreatedNotificationAsync(
        Guid projectId,
        string projectName,
        string? address = null,
        ProjectStatus status = ProjectStatus.Planning,
        decimal? latitude = null,
        decimal? longitude = null,
        string? createdBy = null)
    {
        try
        {
            var projectData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                Address = address,
                Status = status.ToString(),
                Coordinates = latitude.HasValue && longitude.HasValue ? new
                {
                    Latitude = latitude,
                    Longitude = longitude
                } : null,
                CreatedBy = createdBy,
                Timestamp = DateTime.UtcNow,
                Type = "ProjectCreated"
            };

            // Send to all users for dashboard updates
            await _hubContext.Clients.All
                .SendAsync("ProjectCreated", projectData);

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("ProjectCreated", projectData);

            // Send to regional groups if coordinates are provided
            if (latitude.HasValue && longitude.HasValue)
            {
                var region = DetermineThailandRegion(latitude.Value, longitude.Value);
                await _hubContext.Clients.Group($"region_{region}")
                    .SendAsync("RegionalProjectUpdate", new
                    {
                        Region = region,
                        Projects = new[] { projectData }
                    });
            }

            // Send universal entity creation event
            await _hubContext.Clients.All
                .SendAsync("EntityCreated", new
                {
                    EntityType = "Project",
                    EntityId = projectId,
                    Data = projectData
                });

            _logger.LogInformation("Sent enhanced project created notification for {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send enhanced project created notification for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send enhanced project update notification with comprehensive change tracking
    /// </summary>
    public async System.Threading.Tasks.Task SendEnhancedProjectUpdatedNotificationAsync(
        Guid projectId,
        string projectName,
        string? address = null,
        ProjectStatus? status = null,
        decimal? latitude = null,
        decimal? longitude = null,
        DateTime? actualEndDate = null,
        decimal? completionPercentage = null,
        string? updatedBy = null,
        Dictionary<string, object>? changedFields = null)
    {
        try
        {
            var updateData = new
            {
                ProjectId = projectId,
                ProjectName = projectName,
                Address = address,
                Status = status?.ToString(),
                Coordinates = latitude.HasValue && longitude.HasValue ? new
                {
                    Latitude = latitude,
                    Longitude = longitude
                } : null,
                ActualEndDate = actualEndDate,
                CompletionPercentage = completionPercentage,
                UpdatedBy = updatedBy,
                ChangedFields = changedFields,
                Timestamp = DateTime.UtcNow,
                Type = "ProjectUpdated"
            };

            // Send to project-specific group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("ProjectUpdated", updateData);

            // Send to managers and admins
            await _hubContext.Clients.Groups(new[] { "role_manager", "role_administrator" })
                .SendAsync("ProjectUpdated", updateData);

            // Send to regional groups if coordinates are provided
            if (latitude.HasValue && longitude.HasValue)
            {
                var region = DetermineThailandRegion(latitude.Value, longitude.Value);
                await _hubContext.Clients.Group($"region_{region}")
                    .SendAsync("RegionalProjectUpdate", new
                    {
                        Region = region,
                        ProjectId = projectId,
                        UpdateData = updateData
                    });
            }

            // Send universal entity update
            await _hubContext.Clients.All
                .SendAsync("EntityUpdated", new
                {
                    EntityType = "Project",
                    EntityId = projectId,
                    ChangeType = "Updated",
                    Data = updateData
                });

            _logger.LogInformation("Sent enhanced project updated notification for {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send enhanced project updated notification for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Send live dashboard statistics update
    /// </summary>
    public async System.Threading.Tasks.Task SendDashboardStatsUpdatedNotificationAsync()
    {
        try
        {
            // Calculate current project statistics
            var totalProjects = await _context.Projects.CountAsync();
            var statusBreakdown = await _context.Projects
                .GroupBy(p => p.Status)
                .Select(g => new { Status = g.Key.ToString(), Count = g.Count() })
                .ToListAsync();

            var completedCount = statusBreakdown.FirstOrDefault(s => s.Status == "Completed")?.Count ?? 0;
            var inProgressCount = statusBreakdown.FirstOrDefault(s => s.Status == "InProgress")?.Count ?? 0;
            var planningCount = statusBreakdown.FirstOrDefault(s => s.Status == "Planning")?.Count ?? 0;

            // Calculate regional statistics
            var projectsWithCoordinates = await _context.Projects
                .Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
                .Select(p => new { p.ProjectId, p.Status, p.Latitude, p.Longitude })
                .ToListAsync();

            var regionalStats = new
            {
                Northern = CalculateRegionalStats(projectsWithCoordinates, "northern"),
                Western = CalculateRegionalStats(projectsWithCoordinates, "western"),
                Central = CalculateRegionalStats(projectsWithCoordinates, "central")
            };

            var statsData = new
            {
                TotalProjects = totalProjects,
                StatusBreakdown = new
                {
                    Completed = completedCount,
                    InProgress = inProgressCount,
                    Planning = planningCount
                },
                CompletionPercentage = totalProjects > 0 ? (int)Math.Round((double)completedCount / totalProjects * 100) : 0,
                RegionalStats = regionalStats,
                Timestamp = DateTime.UtcNow,
                Type = "DashboardStatsUpdated"
            };

            // Send to all connected users
            await _hubContext.Clients.All
                .SendAsync("ProjectStatsUpdated", statsData);

            _logger.LogInformation("Sent dashboard stats update notification");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send dashboard stats update notification");
            throw;
        }
    }

    /// <summary>
    /// Send water facility update notification
    /// </summary>
    public async System.Threading.Tasks.Task SendWaterFacilityUpdateNotificationAsync(
        Guid projectId,
        string facilityType,
        string facilityName,
        Dictionary<string, object> updates)
    {
        try
        {
            var facilityData = new
            {
                ProjectId = projectId,
                FacilityType = facilityType,
                FacilityName = facilityName,
                Updates = updates,
                Timestamp = DateTime.UtcNow,
                Type = "WaterFacilityUpdate"
            };

            // Send to facility-specific group
            await _hubContext.Clients.Group($"facility_{facilityType}")
                .SendAsync("WaterFacilityUpdate", facilityData);

            // Send to project group
            await _hubContext.Clients.Group($"project_{projectId}")
                .SendAsync("WaterFacilityUpdate", facilityData);

            _logger.LogInformation("Sent water facility update notification for {ProjectId} - {FacilityType}", 
                projectId, facilityType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send water facility update notification for {ProjectId}", projectId);
            throw;
        }
    }

    /// <summary>
    /// Determine Thailand region based on GPS coordinates
    /// </summary>
    private static string DetermineThailandRegion(decimal latitude, decimal longitude)
    {
        // Northern Thailand (approximately)
        if (latitude >= 18.0m && latitude <= 20.5m && longitude >= 98.0m && longitude <= 101.5m)
            return "northern";
        
        // Western Thailand (approximately)
        if (latitude >= 15.5m && latitude <= 18.0m && longitude >= 98.0m && longitude <= 99.5m)
            return "western";
        
        // Central Thailand (including Bangkok)
        if (latitude >= 13.0m && latitude <= 18.0m && longitude >= 99.5m && longitude <= 102.0m)
            return "central";
        
        // Default to central for any coordinates that don't match specific regions
        return "central";
    }

    /// <summary>
    /// Calculate regional statistics for projects
    /// </summary>
    private static object CalculateRegionalStats<T>(
        List<T> projectsWithCoordinates, 
        string region) where T : class
    {
        var regionalProjects = projectsWithCoordinates
            .Where(p => {
                var latitude = (decimal?)p.GetType().GetProperty("Latitude")?.GetValue(p);
                var longitude = (decimal?)p.GetType().GetProperty("Longitude")?.GetValue(p);
                return latitude.HasValue && longitude.HasValue && 
                       DetermineThailandRegion(latitude.Value, longitude.Value) == region;
            })
            .ToList();

        var total = regionalProjects.Count;
        var completed = regionalProjects.Count(p => {
            var status = p.GetType().GetProperty("Status")?.GetValue(p);
            return status?.ToString() == "Completed";
        });

        return new { Total = total, Completed = completed };
    }

    #endregion
}
