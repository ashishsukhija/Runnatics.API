using System.Linq.Expressions;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Runnatics.Data.EF;
using Runnatics.Models.Client.Requests.Certificates;
using Runnatics.Models.Client.Responses.Certificates;
using Runnatics.Models.Data.Entities;
using Runnatics.Models.Data.Enumerations;
using Runnatics.Repositories.Interface;
using Runnatics.Services.Interface;
using SkiaSharp;

namespace Runnatics.Services
{
    public class CertificatesService(
        IUnitOfWork<RaceSyncDbContext> repository,
        IMapper mapper,
        ILogger<CertificatesService> logger,
        IUserContextService userContext,
        IEncryptionService encryptionService,
        IHttpClientFactory httpClientFactory) : ServiceBase<IUnitOfWork<RaceSyncDbContext>>(repository), ICertificatesService
    {
        private readonly IMapper _mapper = mapper;
        private readonly ILogger<CertificatesService> _logger = logger;
        private readonly IUserContextService _userContext = userContext;
        private readonly IEncryptionService _encryptionService = encryptionService;
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;

        private static Expression<Func<CertificateTemplate, bool>> IsActiveFilter =>
            t => t.AuditProperties.IsActive && !t.AuditProperties.IsDeleted;

        public async Task<CertificateTemplateResponse?> CreateTemplateAsync(CertificateTemplateRequest request)
        {
            try
            {
                var decryptedEventId = TryParseOrDecrypt(request.EventId, nameof(request.EventId));
                int? decryptedRaceId = null;

                if (!string.IsNullOrEmpty(request.RaceId))
                {
                    decryptedRaceId = TryParseOrDecrypt(request.RaceId, nameof(request.RaceId));
                }

                if (!await EventExistsAsync(decryptedEventId))
                {
                    ErrorMessage = "Event not found or inactive.";
                    return null;
                }

                if (decryptedRaceId.HasValue && !await RaceExistsAsync(decryptedEventId, decryptedRaceId.Value))
                {
                    ErrorMessage = "Race not found or inactive.";
                    return null;
                }

                var currentUserId = GetCurrentUserId();
                var templateRepo = _repository.GetRepository<CertificateTemplate>();

                // If this template is being set as default, unmark any existing default templates for the event
                if (request.IsDefault)
                {
                    await UnmarkExistingDefaultTemplatesAsync(decryptedEventId, currentUserId);
                }

                var template = new CertificateTemplate
                {
                    EventId = decryptedEventId,
                    RaceId = decryptedRaceId,
                    Name = request.Name,
                    Description = request.Description,
                    Width = request.Width,
                    Height = request.Height,
                    BackgroundImageData = request.BackgroundImageData,
                    IsDefault = request.IsDefault,
                    //IsActive = request.IsActive,
                    AuditProperties = CreateAuditProperties(currentUserId)
                };

                await templateRepo.AddAsync(template);
                await _repository.SaveChangesAsync();

                if (request.Fields.Any())
                {
                    var fieldRepo = _repository.GetRepository<CertificateField>();
                    var fields = request.Fields.Select(f => new CertificateField
                    {
                        TemplateId = template.Id,
                        FieldType = f.FieldType,
                        Content = f.Content,
                        XCoordinate = f.XCoordinate,
                        YCoordinate = f.YCoordinate,
                        Font = f.Font,
                        FontSize = f.FontSize,
                        FontColor = f.FontColor,
                        Width = f.Width,
                        Height = f.Height,
                        Alignment = f.Alignment ?? "left",
                        FontWeight = f.FontWeight ?? "normal",
                        FontStyle = f.FontStyle ?? "normal",
                        AuditProperties = CreateAuditProperties(currentUserId)
                    }).ToList();

                    await fieldRepo.AddRangeAsync(fields);
                    await _repository.SaveChangesAsync();
                }

                _logger.LogInformation("Certificate template created. Id: {Id}, EventId: {EventId}, CreatedBy: {UserId}",
                    template.Id, template.EventId, currentUserId);

                return await GetTemplateAsync(_encryptionService.Encrypt(template.Id.ToString()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating certificate template for EventId: {EventId}", request.EventId);
                ErrorMessage = "Error creating certificate template.";
                return null;
            }
        }

        public async Task<CertificateTemplateResponse?> UpdateTemplateAsync(string id, CertificateTemplateRequest request)
        {
            try
            {
                var decryptedId = TryParseOrDecrypt(id, nameof(id));
                var decryptedEventId = TryParseOrDecrypt(request.EventId, nameof(request.EventId));
                int? decryptedRaceId = null;

                if (!string.IsNullOrEmpty(request.RaceId))
                {
                    decryptedRaceId = TryParseOrDecrypt(request.RaceId, nameof(request.RaceId));
                }

                var templateRepo = _repository.GetRepository<CertificateTemplate>();
                var existing = await templateRepo
                    .GetQuery(t => t.Id == decryptedId)
                    .Where(IsActiveFilter)
                    .FirstOrDefaultAsync();

                if (existing == null)
                {
                    ErrorMessage = "Certificate template not found.";
                    _logger.LogWarning("Certificate template not found: {Id}", id);
                    return null;
                }

                if (!await EventExistsAsync(decryptedEventId))
                {
                    ErrorMessage = "Event not found or inactive.";
                    return null;
                }

                if (decryptedRaceId.HasValue && !await RaceExistsAsync(decryptedEventId, decryptedRaceId.Value))
                {
                    ErrorMessage = "Race not found or inactive.";
                    return null;
                }

                var currentUserId = GetCurrentUserId();

                // If this template is being set as default, unmark any existing default templates for the event
                if (request.IsDefault && !existing.IsDefault)
                {
                    await UnmarkExistingDefaultTemplatesAsync(decryptedEventId, currentUserId, existing.Id);
                }

                existing.EventId = decryptedEventId;
                existing.RaceId = decryptedRaceId;
                existing.Name = request.Name;
                existing.Description = request.Description;
                existing.Width = request.Width;
                existing.Height = request.Height;
                existing.BackgroundImageData = request.BackgroundImageData;
                existing.IsDefault = request.IsDefault;
                //existing.IsActive = request.IsActive;
                existing.AuditProperties.UpdatedDate = DateTime.UtcNow;
                existing.AuditProperties.UpdatedBy = currentUserId;

                // Soft-delete ALL existing active fields for this template
                var fieldRepo = _repository.GetRepository<CertificateField>();
                var allExistingFields = await fieldRepo
                    .GetQuery(f => f.TemplateId == existing.Id && f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted)
                    .ToListAsync();
                
                foreach (var existingField in allExistingFields)
                {
                    existingField.AuditProperties.IsDeleted = true;
                    existingField.AuditProperties.IsActive = false;
                    existingField.AuditProperties.UpdatedDate = DateTime.UtcNow;
                    existingField.AuditProperties.UpdatedBy = currentUserId;
                }

                // Update the soft-deleted fields
                if (allExistingFields.Count != 0)
                {
                    await fieldRepo.UpdateRangeAsync(allExistingFields);
                }

                // Insert new fields from the request with AuditProperties
                var newFields = request.Fields.Select(f => new CertificateField
                {
                    TemplateId = existing.Id,
                    FieldType = f.FieldType,
                    Content = f.Content,
                    XCoordinate = f.XCoordinate,
                    YCoordinate = f.YCoordinate,
                    Font = f.Font,
                    FontSize = f.FontSize,
                    FontColor = f.FontColor,
                    Width = f.Width,
                    Height = f.Height,
                    Alignment = f.Alignment ?? "left",
                    FontWeight = f.FontWeight ?? "normal",
                    FontStyle = f.FontStyle ?? "normal",
                    AuditProperties = CreateAuditProperties(currentUserId)
                }).ToList();

                await fieldRepo.AddRangeAsync(newFields);
                await templateRepo.UpdateAsync(existing);
                await _repository.SaveChangesAsync();

                _logger.LogInformation("Certificate template updated. Id: {Id}", decryptedId);

                return await GetTemplateAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating certificate template: {Id}", id);
                ErrorMessage = "Error updating certificate template.";
                return null;
            }
        }

        public async Task<CertificateTemplateResponse?> GetTemplateAsync(string id)
        {
            try
            {
                var decryptedId = TryParseOrDecrypt(id, nameof(id));
                var templateRepo = _repository.GetRepository<CertificateTemplate>();

                var template = await templateRepo
                    .GetQuery(t => t.Id == decryptedId)
                    .Where(IsActiveFilter)
                    .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (template == null)
                {
                    ErrorMessage = "Certificate template not found.";
                    return null;
                }

                return MapToResponse(template);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving certificate template: {Id}", id);
                ErrorMessage = "Error retrieving certificate template.";
                return null;
            }
        }

        public async Task<List<CertificateTemplateResponse>> GetTemplatesByEventAsync(string eventId)
        {
            try
            {
                var decryptedEventId = TryParseOrDecrypt(eventId, nameof(eventId));
                var templateRepo = _repository.GetRepository<CertificateTemplate>();

                var templates = await templateRepo
                    .GetQuery(t => t.EventId == decryptedEventId)
                    .Where(IsActiveFilter)
                    .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                    .AsNoTracking()
                    .OrderBy(t => t.AuditProperties.CreatedDate)
                    .ToListAsync();

                return templates.Select(MapToResponse).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving certificate templates for EventId: {EventId}", eventId);
                ErrorMessage = "Error retrieving certificate templates.";
                return new List<CertificateTemplateResponse>();
            }
        }

        public async Task<CertificateTemplateResponse?> GetTemplateByRaceAsync(string eventId, string raceId)
        {
            try
            {
                var decryptedEventId = TryParseOrDecrypt(eventId, nameof(eventId));
                var decryptedRaceId = TryParseOrDecrypt(raceId, nameof(raceId));
                var templateRepo = _repository.GetRepository<CertificateTemplate>();

                var template = await templateRepo
                    .GetQuery(t => t.EventId == decryptedEventId && t.RaceId == decryptedRaceId)
                    .Where(IsActiveFilter)
                    .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (template == null)
                {
                    // Try to find the default template for the event
                    var defaultTemplate = await templateRepo
                        .GetQuery(t => t.EventId == decryptedEventId && t.IsDefault)
                        .Where(IsActiveFilter)
                        .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                        .AsNoTracking()
                        .FirstOrDefaultAsync();

                    if (defaultTemplate != null)
                    {
                        return MapToResponse(defaultTemplate);
                    }

                    // Fallback to any event-wide template (RaceId is null)
                    var eventWideTemplate = await templateRepo
                        .GetQuery(t => t.EventId == decryptedEventId && t.RaceId == null)
                        .Where(IsActiveFilter)
                        .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                        .AsNoTracking()
                        .FirstOrDefaultAsync();

                    if (eventWideTemplate == null)
                    {
                        ErrorMessage = "No certificate template found for this race or event.";
                        return null;
                    }

                    return MapToResponse(eventWideTemplate);
                }

                return MapToResponse(template);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving certificate template for Race: {RaceId}, Event: {EventId}", raceId, eventId);
                ErrorMessage = "Error retrieving certificate template.";
                return null;
            }
        }

        public async Task<bool> DeleteTemplateAsync(string id)
        {
            try
            {
                var decryptedId = TryParseOrDecrypt(id, nameof(id));
                var templateRepo = _repository.GetRepository<CertificateTemplate>();

                var template = await templateRepo
                    .GetQuery(t => t.Id == decryptedId)
                    .Where(IsActiveFilter)
                    .FirstOrDefaultAsync();

                if (template == null)
                {
                    ErrorMessage = "Certificate template not found.";
                    _logger.LogWarning("Certificate template delete failed - not found: {Id}", id);
                    return false;
                }

                template.AuditProperties.IsActive = false;
                template.AuditProperties.IsDeleted = true;
                template.AuditProperties.UpdatedDate = DateTime.UtcNow;
                template.AuditProperties.UpdatedBy = _userContext.UserId;

                await templateRepo.UpdateAsync(template);
                await _repository.SaveChangesAsync();

                _logger.LogInformation("Certificate template deleted. Id: {Id}", decryptedId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting certificate template: {Id}", id);
                ErrorMessage = "Error deleting certificate template.";
                return false;
            }
        }

        // ── Certificate Generation ────────────────────────────────────────────

        public async Task<byte[]?> GenerateParticipantCertificateAsync(string participantId, string raceId, string eventId)
        {
            try
            {
                var decryptedParticipantId = TryParseOrDecrypt(participantId, nameof(participantId));
                var decryptedRaceId        = TryParseOrDecrypt(raceId,        nameof(raceId));
                var decryptedEventId       = TryParseOrDecrypt(eventId,       nameof(eventId));

                // 1. Fetch participant with event, race and result navigation data
                var participantRepo = _repository.GetRepository<Participant>();
                var participant = await participantRepo
                    .GetQuery(p => p.Id == decryptedParticipantId
                                && p.RaceId == decryptedRaceId
                                && p.EventId == decryptedEventId
                                && p.AuditProperties.IsActive
                                && !p.AuditProperties.IsDeleted)
                    .Include(p => p.Event)
                    .Include(p => p.Race)
                    .Include(p => p.Result)
                    .AsNoTracking()
                    .FirstOrDefaultAsync();

                if (participant == null)
                {
                    ErrorMessage = "Participant not found.";
                    return null;
                }

                if (participant.Result == null)
                {
                    _logger.LogWarning(
                        "No result record for participant {ParticipantId}; timing fields will be empty.",
                        decryptedParticipantId);
                }

                // 2. Resolve template: race-specific → event default → any event-wide
                var template = await ResolveTemplateForRaceAsync(decryptedEventId, decryptedRaceId);
                if (template == null)
                {
                    ErrorMessage = "No certificate template found for this event.";
                    return null;
                }

                // 3. Load background image bytes (base64 data or URL)
                var backgroundBytes = await ResolveBackgroundImageAsync(template);

                // 4. Build field value dictionary
                var fieldData = BuildFieldData(participant, participant.Result, participant.Race, participant.Event);

                // 5. Render to PNG
                return RenderToPng(template, fieldData, backgroundBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating certificate for participant {ParticipantId}", participantId);
                ErrorMessage = "Error generating certificate.";
                return null;
            }
        }

        // ── Certificate generation helpers ────────────────────────────────────

        /// <summary>
        /// Resolves the best-matching template entity (race-specific → default → event-wide).
        /// Mirrors the fallback logic in GetTemplateByRaceAsync but returns the raw entity.
        /// </summary>
        private async Task<CertificateTemplate?> ResolveTemplateForRaceAsync(int eventId, int raceId)
        {
            var templateRepo = _repository.GetRepository<CertificateTemplate>();

            // 1. Race-specific template
            var template = await templateRepo
                .GetQuery(t => t.EventId == eventId && t.RaceId == raceId)
                .Where(IsActiveFilter)
                .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (template != null) return template;

            // 2. Default event template
            template = await templateRepo
                .GetQuery(t => t.EventId == eventId && t.IsDefault)
                .Where(IsActiveFilter)
                .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                .AsNoTracking()
                .FirstOrDefaultAsync();

            if (template != null) return template;

            // 3. Any event-wide template (no specific race)
            return await templateRepo
                .GetQuery(t => t.EventId == eventId && t.RaceId == null)
                .Where(IsActiveFilter)
                .Include(t => t.Fields.Where(f => f.AuditProperties.IsActive && !f.AuditProperties.IsDeleted))
                .AsNoTracking()
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Returns background image bytes from base64 data or by fetching the URL.
        /// Returns null if neither is available or decoding fails.
        /// </summary>
        private async Task<byte[]?> ResolveBackgroundImageAsync(CertificateTemplate template)
        {
            if (!string.IsNullOrWhiteSpace(template.BackgroundImageData))
            {
                try
                {
                    var base64 = StripBase64DataUrlPrefix(template.BackgroundImageData);
                    return Convert.FromBase64String(base64);
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "BackgroundImageData for template {TemplateId} is not valid base64", template.Id);
                }
            }

            if (!string.IsNullOrWhiteSpace(template.BackgroundImageUrl))
            {
                try
                {
                    using var client = _httpClientFactory.CreateClient();
                    return await client.GetByteArrayAsync(template.BackgroundImageUrl);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch background image URL for template {TemplateId}", template.Id);
                }
            }

            return null;
        }

        /// <summary>
        /// Maps participant / result / race / event data to the CertificateFieldType dictionary
        /// that the renderer uses to substitute placeholder values.
        /// </summary>
        private static Dictionary<CertificateFieldType, string> BuildFieldData(
            Participant participant,
            Results? result,
            Race race,
            Event evt)
        {
            var chipTime = result?.NetTime.HasValue == true
                ? TimeSpan.FromMilliseconds(result.NetTime.Value)
                : (TimeSpan?)null;

            var gunTime = result?.GunTime.HasValue == true
                ? TimeSpan.FromMilliseconds(result.GunTime.Value)
                : (TimeSpan?)null;

            return new Dictionary<CertificateFieldType, string>
            {
                [CertificateFieldType.ParticipantName] = participant.FullName.Trim(),
                [CertificateFieldType.ChipTime]        = chipTime.HasValue ? FormatTimeSpan(chipTime.Value) : "--:--:--",
                [CertificateFieldType.GunTime]         = gunTime.HasValue  ? FormatTimeSpan(gunTime.Value)  : "--:--:--",
                [CertificateFieldType.BibNumber]       = participant.BibNumber  ?? string.Empty,
                [CertificateFieldType.GenderRank]      = result?.GenderRank?.ToString()  ?? "-",
                [CertificateFieldType.OverallRank]     = result?.OverallRank?.ToString() ?? "-",
                [CertificateFieldType.CategoryRank]    = result?.CategoryRank?.ToString() ?? "-",
                [CertificateFieldType.RaceCategory]    = race.Title,
                [CertificateFieldType.Category]        = participant.AgeCategory ?? string.Empty,
                [CertificateFieldType.Gender]          = participant.Gender ?? string.Empty,
                [CertificateFieldType.TimeHrs]         = chipTime.HasValue ? ((int)chipTime.Value.TotalHours).ToString("D2") : "--",
                [CertificateFieldType.TimeMins]        = chipTime.HasValue ? chipTime.Value.Minutes.ToString("D2") : "--",
                [CertificateFieldType.TimeSecs]        = chipTime.HasValue ? chipTime.Value.Seconds.ToString("D2") : "--",
                [CertificateFieldType.Distance]        = race.Distance.HasValue ? $"{race.Distance.Value:0.##} KM" : string.Empty,
                [CertificateFieldType.EventName]       = evt.Name,
                [CertificateFieldType.EventDate]       = evt.EventDate.ToString("dd MMMM yyyy"),
            };
        }

        /// <summary>
        /// Renders the certificate template to a PNG using SkiaSharp.
        /// YCoordinate is the text baseline. A field with a Width is treated as a box spanning
        /// [XCoordinate, XCoordinate + Width]: the text is anchored inside it per Alignment and
        /// fitted to it (see <see cref="FitTextToWidth"/>) so it can never overrun the box. A field
        /// with no Width keeps the legacy behaviour — XCoordinate is the anchor, nothing is fitted.
        /// The Photo field type is skipped — the Participant entity has no photo property.
        /// </summary>
        private static byte[] RenderToPng(
            CertificateTemplate template,
            Dictionary<CertificateFieldType, string> fieldData,
            byte[]? backgroundBytes)
        {
            using var bitmap = new SKBitmap(template.Width, template.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);

            canvas.Clear(SKColors.White);

            // Draw background image
            if (backgroundBytes != null)
            {
                using var bgBitmap = SKBitmap.Decode(backgroundBytes);
                if (bgBitmap != null)
                {
                    canvas.DrawBitmap(bgBitmap, SKRect.Create(0, 0, template.Width, template.Height));
                }
            }

            // Draw each field
            foreach (var field in template.Fields)
            {
                // Photo requires a participant image asset — not available on the entity; skip
                if (field.FieldType == CertificateFieldType.Photo)
                    continue;

                // CustomText renders field.Content verbatim; all other types look up the data dict
                var text = field.FieldType == CertificateFieldType.CustomText
                    ? (field.Content ?? string.Empty)
                    : fieldData.GetValueOrDefault(field.FieldType, string.Empty);

                if (string.IsNullOrEmpty(text)) continue;

                var weight = string.Equals(field.FontWeight, "bold", StringComparison.OrdinalIgnoreCase)
                    ? SKFontStyleWeight.Bold
                    : SKFontStyleWeight.Normal;

                var slant = string.Equals(field.FontStyle, "italic", StringComparison.OrdinalIgnoreCase)
                    ? SKFontStyleSlant.Italic
                    : SKFontStyleSlant.Upright;

                using var typeface = SKTypeface.FromFamilyName(field.Font, weight, SKFontStyleWidth.Normal, slant)
                                  ?? SKTypeface.Default;

                var textAlign = field.Alignment?.ToLowerInvariant() switch
                {
                    "center" => SKTextAlign.Center,
                    "right"  => SKTextAlign.Right,
                    _        => SKTextAlign.Left
                };

#pragma warning disable CS0618 // TextAlign is deprecated in SkiaSharp 3.x but is correct for 2.88.x
                using var paint = new SKPaint
                {
                    Color       = ParseSkiaColor(field.FontColor),
                    TextSize    = field.FontSize,
                    IsAntialias = true,
                    Typeface    = typeface,
                    TextAlign   = textAlign
                };
#pragma warning restore CS0618

                // A field with a Width is a BOX: XCoordinate is its left edge and the text is
                // anchored inside [X, X+Width] per Alignment, then fitted to that width so a
                // long value can never run past the box. A field without a Width keeps the
                // legacy behaviour exactly — XCoordinate is the anchor and nothing is fitted.
                var drawX = (float)field.XCoordinate;

                if (field.Width is > 0)
                {
                    var boxWidth = (float)field.Width.Value;

                    drawX = textAlign switch
                    {
                        SKTextAlign.Center => field.XCoordinate + boxWidth / 2f,
                        SKTextAlign.Right  => field.XCoordinate + boxWidth,
                        _                  => field.XCoordinate
                    };

                    text = FitTextToWidth(paint, text, boxWidth);
                }

                canvas.DrawText(text, drawX, field.YCoordinate, paint);
            }

            using var image   = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded.ToArray();
        }

        /// <summary>
        /// Smallest fraction of the configured font size that auto-shrink may fall back to.
        /// A participant's name should stay whole on their own certificate, so we shrink well
        /// before we ever consider cutting characters.
        /// </summary>
        private const float MinAutoShrinkScale = 0.6f;

        private const string Ellipsis = "…";

        /// <summary>
        /// Fits <paramref name="text"/> into <paramref name="maxWidth"/>: shrinks the font down to
        /// <see cref="MinAutoShrinkScale"/> of its configured size first, and only ellipsis-truncates
        /// if the text still does not fit at that floor. Mutates <paramref name="paint"/>.TextSize.
        /// Returns the string to draw.
        /// </summary>
        private static string FitTextToWidth(SKPaint paint, string text, float maxWidth)
        {
            if (maxWidth <= 0 || string.IsNullOrEmpty(text)) return text;

            var configuredSize = paint.TextSize;
            var measured = paint.MeasureText(text);
            if (measured <= maxWidth) return text;

            // Glyph advances scale linearly with TextSize, so one ratio gets us essentially there.
            var floor = configuredSize * MinAutoShrinkScale;
            paint.TextSize = Math.Max(configuredSize * (maxWidth / measured), floor);

            // Hinting can round a pixel or two the wrong way — nudge down until it truly fits.
            while (paint.MeasureText(text) > maxWidth && paint.TextSize > floor)
                paint.TextSize = Math.Max(paint.TextSize - 0.5f, floor);

            if (paint.MeasureText(text) <= maxWidth) return text;

            // Still over at the shrink floor: trim characters and mark the cut.
            for (var len = text.Length - 1; len > 0; len--)
            {
                var candidate = text[..len].TrimEnd() + Ellipsis;
                if (paint.MeasureText(candidate) <= maxWidth) return candidate;
            }

            return Ellipsis;
        }

        private static string FormatTimeSpan(TimeSpan ts) =>
            $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

        private static SKColor ParseSkiaColor(string? colorHex)
        {
            if (string.IsNullOrWhiteSpace(colorHex)) return SKColors.Black;
            var hex = colorHex.StartsWith('#') ? colorHex : $"#{colorHex}";
            return SKColor.TryParse(hex, out var parsed) ? parsed : SKColors.Black;
        }

        private static string StripBase64DataUrlPrefix(string input)
        {
            var commaIdx = input.IndexOf(',');
            return commaIdx >= 0 ? input[(commaIdx + 1)..] : input;
        }

        #region Helpers

        private int GetCurrentUserId() => _userContext?.IsAuthenticated == true ? _userContext.UserId : 0;

        private int TryParseOrDecrypt(string input, string inputName)
        {
            if (string.IsNullOrEmpty(input))
                throw new ArgumentException("Id input cannot be null or empty", inputName);

            if (int.TryParse(input, out var id))
                return id;

            try
            {
                var decrypted = _encryptionService.Decrypt(input);
                if (int.TryParse(decrypted, out id))
                    return id;

                _logger.LogDebug("Decrypted value for {InputName} did not parse as int", inputName);
                throw new ArgumentException($"Invalid {inputName} format");
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                _logger.LogDebug(ex, "Failed to parse or decrypt input for {InputName}", inputName);
                throw new ArgumentException($"Invalid {inputName} format", inputName, ex);
            }
        }

        private async Task<bool> EventExistsAsync(int eventId)
        {
            var eventRepo = _repository.GetRepository<Event>();
            return await eventRepo
                .GetQuery(e => e.Id == eventId)
                .Where(e => e.AuditProperties.IsActive && !e.AuditProperties.IsDeleted)
                .AsNoTracking()
                .AnyAsync();
        }

        private async Task<bool> RaceExistsAsync(int eventId, int raceId)
        {
            var raceRepo = _repository.GetRepository<Race>();
            return await raceRepo
                .GetQuery(r => r.Id == raceId && r.EventId == eventId)
                .Where(r => r.AuditProperties.IsActive && !r.AuditProperties.IsDeleted)
                .AsNoTracking()
                .AnyAsync();
        }

        private CertificateTemplateResponse MapToResponse(CertificateTemplate template)
        {
            return new CertificateTemplateResponse
            {
                Id = _encryptionService.Encrypt(template.Id.ToString()),
                EventId = _encryptionService.Encrypt(template.EventId.ToString()),
                RaceId = template.RaceId.HasValue ? _encryptionService.Encrypt(template.RaceId.Value.ToString()) : null,
                Name = template.Name,
                Description = template.Description,
                BackgroundImageUrl = template.BackgroundImageUrl,
                BackgroundImageData = template.BackgroundImageData,
                Width = template.Width,
                Height = template.Height,
                IsDefault = template.IsDefault,
                IsActive = template.AuditProperties.IsActive,
                CreatedAt = template.AuditProperties.CreatedDate,
                UpdatedAt = template.AuditProperties.UpdatedDate,
                Fields = template.Fields.Select(f => new CertificateFieldResponse
                {
                    Id = _encryptionService.Encrypt(f.Id.ToString()),
                    FieldType = f.FieldType,
                    Content = f.Content,
                    XCoordinate = f.XCoordinate,
                    YCoordinate = f.YCoordinate,
                    Font = f.Font,
                    FontSize = f.FontSize,
                    FontColor = f.FontColor,
                    Width = f.Width,
                    Height = f.Height,
                    Alignment = f.Alignment,
                    FontWeight = f.FontWeight,
                    FontStyle = f.FontStyle
                }).ToList()
            };
        }

        private static Models.Data.Common.AuditProperties CreateAuditProperties(int userId)
        {
            return new Models.Data.Common.AuditProperties
            {
                IsActive = true,
                IsDeleted = false,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = userId
            };
        }

        /// <summary>
        /// Unmarks all existing default templates for a given event
        /// </summary>
        /// <param name="eventId">The event ID</param>
        /// <param name="currentUserId">The current user ID for audit tracking</param>
        /// <param name="excludeTemplateId">Optional template ID to exclude from unmarking (used during updates)</param>
        private async Task UnmarkExistingDefaultTemplatesAsync(int eventId, int currentUserId, int? excludeTemplateId = null)
        {
            var templateRepo = _repository.GetRepository<CertificateTemplate>();
            
            var existingDefaultTemplates = await templateRepo
                .GetQuery(t => t.EventId == eventId && t.IsDefault)
                .Where(IsActiveFilter)
                .ToListAsync();

            if (excludeTemplateId.HasValue)
            {
                existingDefaultTemplates = existingDefaultTemplates
                    .Where(t => t.Id != excludeTemplateId.Value)
                    .ToList();
            }

            foreach (var template in existingDefaultTemplates)
            {
                template.IsDefault = false;
                template.AuditProperties.UpdatedDate = DateTime.UtcNow;
                template.AuditProperties.UpdatedBy = currentUserId;
            }

            if (existingDefaultTemplates.Any())
            {
                await templateRepo.UpdateRangeAsync(existingDefaultTemplates);
                _logger.LogInformation("Unmarked {Count} existing default templates for EventId: {EventId}",
                    existingDefaultTemplates.Count, eventId);
            }
        }

        #endregion
    }
}
