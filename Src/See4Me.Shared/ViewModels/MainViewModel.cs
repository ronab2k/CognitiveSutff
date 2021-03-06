﻿using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Vision;
using See4Me.Common;
using See4Me.Localization.Resources;
using See4Me.Services;
using See4Me.Services.Translator;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using See4Me.Extensions;
using System.IO;
using System.Text;
using System.Net;
using Microsoft.ProjectOxford.Vision.Contract;

namespace See4Me.ViewModels
{
    public partial class MainViewModel : ViewModelBase
    {
        private readonly IStreamingService streamingService;
        private readonly ISpeechService speechService;

        private CameraPanel lastCameraPanel = CameraPanel.Unknown;
        private bool initialized = false;

        public bool IsVisionServiceRegistered => !string.IsNullOrWhiteSpace(ServiceKeys.VisionSubscriptionKey);

        public bool IsEmotionServiceRegistered => !string.IsNullOrWhiteSpace(ServiceKeys.EmotionSubscriptionKey);

        public bool IsTranslatorServiceRegistered
            => !string.IsNullOrWhiteSpace(ServiceKeys.TranslatorClientId) && !string.IsNullOrWhiteSpace(ServiceKeys.TranslatorClientSecret);

        private string statusMessage;
        public string StatusMessage
        {
            get { return statusMessage; }
			set { this.Set(ref statusMessage, value, true); }
        }

        public AutoRelayCommand DescribeImageCommand { get; set; }

        public AutoRelayCommand SwapCameraCommand { get; set; }

        public AutoRelayCommand GotoSettingsCommand { get; set; }

        public AutoRelayCommand GotoRecognizeTextCommand { get; set; }

        public MainViewModel(IStreamingService streamingService, ISpeechService speechService)
        {
            this.streamingService = streamingService;
            this.speechService = speechService;

            this.CreateCommands();

            // Initializes vision extensions.
            var visionInitializeTask = VisionExtensions.InitializeAsync();
        }

        private void CreateCommands()
        {
            DescribeImageCommand = new AutoRelayCommand(async () => await DescribeImageAsync(), () => IsVisionServiceRegistered && !IsBusy)
                .DependsOn(() => IsBusy);

            SwapCameraCommand = new AutoRelayCommand(async () => await SwapCameraAsync(), () => IsVisionServiceRegistered && !IsBusy)
                .DependsOn(() => IsBusy);

            GotoSettingsCommand = new AutoRelayCommand(() => Navigator.NavigateTo(Pages.SettingsPage.ToString()));

            GotoRecognizeTextCommand = new AutoRelayCommand(() => Navigator.NavigateTo(Pages.RecognizeTextPage.ToString()), () => IsVisionServiceRegistered && !IsBusy)
                .DependsOn(() => IsBusy);
        }

        public async Task CheckShowConsentAsync()
        {
            // If not given, asks the user for the consent to use the app.
            if (!Settings.IsConsentGiven)
            {
                await DialogService.ShowAsync(AppResources.ConsentRequiredMessage, AppResources.ConsentRequiredTitle);
                Settings.IsConsentGiven = true;
            }
        }

        public async Task InitializeStreamingAsync()
        {
            IsBusy = true;

            try
            {
                // Asks the view the UI element in which to start camera streaming.
                Messenger.Default.Send(new NotificationMessageAction<object>(Constants.InitializeStreaming, async (video) =>
                {
                    var successful = false;

                    try
                    {
                        await streamingService.InitializeAsync();
                        await streamingService.StartStreamingAsync(Settings.CameraPanel, video);

                        successful = (streamingService.CurrentState == ScenarioState.Streaming);
                    }
                    catch
                    { }
                    finally
                    {
                        if (!IsVisionServiceRegistered)
                        {
                            StatusMessage = AppResources.ServiceNotRegistered;
                            await SpeechHelper.TrySpeechAsync(AppResources.ServiceNotRegistered);
                        }
                        else if (successful)
                        {
                            await this.NotifyCameraPanelAsync();
                        }
                        else
                        {
                            await this.NotifyInitializationErrorAsync();
                        }
                    }
                }));
            }
            catch
            {
                await this.NotifyInitializationErrorAsync();
            }

            initialized = true;
            IsBusy = false;
        }

        public async Task CleanupAsync()
        {
            try
            {
                await streamingService.StopStreamingAsync();
                await streamingService.CleanupAsync();
            }
            catch { }
        }

        public async Task DescribeImageAsync()
        {
            IsBusy = true;
            StatusMessage = null;

            var visionService = ViewModelLocator.VisionServiceClient;
            var emotionService = ViewModelLocator.EmotionServiceClient;
            var translatorService = ViewModelLocator.TranslatorService;

            string baseDescription = null;
            string facesRecognizedDescription = null;
            string emotionDescription = null;

            MessengerInstance.Send(new NotificationMessage(Constants.TakingPhoto));

            try
            {
                StatusMessage = AppResources.QueryingVisionService;
                using (var stream = await streamingService.GetCurrentFrameAsync())
                {
                    if (stream != null)
                    {
                        if (await Network.IsInternetAvailableAsync())
                        {
                            var imageBytes = await stream.ToArrayAsync();
                            MessengerInstance.Send(new NotificationMessage<byte[]>(imageBytes, Constants.PhotoTaken));

                            var visualFeatures = new VisualFeature[] { VisualFeature.Description, VisualFeature.Faces };
                            var result = await visionService.AnalyzeImageAsync(stream, visualFeatures);

                            Caption originalDescription;
                            Caption filteredDescription;

                            if (result.IsValid(out originalDescription, out filteredDescription))
                            {
                                baseDescription = filteredDescription.Text;

                                if (Language != Constants.DefaultLanguge && IsTranslatorServiceRegistered)
                                {
                                    // The description needs to be translated.
                                    StatusMessage = AppResources.Translating;
                                    var translation = await translatorService.TranslateAsync(filteredDescription.Text, from: Constants.DefaultLanguge, to: Language);

                                    if (Settings.ShowOriginalDescriptionOnTranslation)
                                        baseDescription = $"{translation} ({filteredDescription.Text})";
                                    else
                                        baseDescription = translation;
                                }

                                if (Settings.ShowDescriptionConfidence)
                                    baseDescription = $"{baseDescription} ({Math.Round(filteredDescription.Confidence, 2)})";

                                try
                                {
                                    // If there is one or more faces, asks the service information about them.
                                    if (IsEmotionServiceRegistered && result.Faces?.Count() > 0)
                                    {
                                        StatusMessage = AppResources.RecognizingFaces;
                                        var messages = new StringBuilder();

                                        foreach (var face in result.Faces)
                                        {
                                            using (var ms = new MemoryStream(imageBytes))
                                            {
                                                var emotions = await emotionService.RecognizeAsync(ms, face.FaceRectangle.ToRectangle());
                                                var bestEmotion = emotions.FirstOrDefault()?.Scores.GetBestEmotion();

                                                // Creates the emotion description text to be speeched (if there are interesting information).
                                                var emotionMessage = SpeechHelper.GetEmotionMessage(face, bestEmotion, includeAge: Settings.GuessAge);
                                                if (!string.IsNullOrWhiteSpace(emotionMessage))
                                                    messages.Append(emotionMessage);
                                            }
                                        }

                                        // Checks if at least one emotion has been actually recognized.
                                        if (messages.Length > 0)
                                        {
                                            // Describes how many faces have been recognized.
                                            if (result.Faces.Count() == 1)
                                                facesRecognizedDescription = AppResources.FaceRecognizedSingular;
                                            else
                                                facesRecognizedDescription = $"{string.Format(AppResources.FacesRecognizedPlural, result.Faces.Count())} {Constants.SentenceEnd}";

                                            emotionDescription = messages.ToString();
                                        }
                                    }
                                }
                                catch (Microsoft.ProjectOxford.Common.ClientException ex) when (ex.Error.Code.ToLower() == "unauthorized")
                                {
                                    // Unable to access the service (tipically, due to invalid registration keys).
                                    baseDescription = AppResources.UnableToAccessService;
                                }
                                catch
                                { }
                            }
                            else
                            {
                                if (Settings.ShowRawDescriptionOnInvalidRecognition && originalDescription != null)
                                    baseDescription = $"{AppResources.RecognitionFailed} ({originalDescription.Text}, {Math.Round(originalDescription.Confidence, 2)})";
                                else
                                    baseDescription = AppResources.RecognitionFailed;
                            }
                        }
                        else
                        {
                            // Internet isn't available, the service cannot be reached.
                            baseDescription = AppResources.NoConnection;
                        }
                    }
                    else
                    {
                        baseDescription = AppResources.UnableToTakePhoto;
                    }
                }
            }
            catch (WebException)
            {
                // Internet isn't available, the service cannot be reached.
                baseDescription = AppResources.NoConnection;
            }
            catch (ClientException)
            {
                // Unable to access the service (tipically, due to invalid registration keys).
                baseDescription = AppResources.UnableToAccessService;
            }
            catch (Exception ex)
            {
                var error = AppResources.RecognitionError;

                if (Settings.ShowExceptionOnError)
                    error = $"{error} ({ex.Message})";

                baseDescription = error;
            }

            // Shows and speaks the result.
            var message = $"{baseDescription}{Constants.SentenceEnd} {facesRecognizedDescription} {emotionDescription}";
            StatusMessage = this.GetNormalizedMessage(message);

            await SpeechHelper.TrySpeechAsync(message);

            IsBusy = false;
        }

        public async Task SwapCameraAsync()
        {
            IsBusy = true;
            var successful = false;

            try
            {
                await streamingService.SwapCameraAsync();
                Settings.CameraPanel = streamingService.CameraPanel;

                successful = (streamingService.CurrentState == ScenarioState.Streaming);
            }
            catch
            { }
            finally
            {
                if (successful)
                    await this.NotifyCameraPanelAsync();
                else
                    await this.NotifySwapCameraErrorAsync();
            }

            IsBusy = false;
        }

        private async Task NotifyCameraPanelAsync()
        {
            // Avoids to notify if the camera panel is the same.
            if (streamingService.CameraPanel != lastCameraPanel)
            {
                var message = streamingService.CameraPanel == CameraPanel.Front ? AppResources.FrontCameraReady : AppResources.BackCameraReady;
                StatusMessage = message;

                await SpeechHelper.TrySpeechAsync(message);
                lastCameraPanel = streamingService.CameraPanel;
            }
        }

        private async Task NotifyInitializationErrorAsync(Exception error = null)
        {
            // If the app is already initialized, skips the notification error.
            if (!initialized)
            {
                var errorMessage = AppResources.InitializationError;
                if (error != null && Settings.ShowExceptionOnError)
                    errorMessage = $"{errorMessage} ({error.Message})";

                StatusMessage = errorMessage;

                await SpeechHelper.TrySpeechAsync(errorMessage);
            }
        }

        private async Task NotifySwapCameraErrorAsync()
        {
            StatusMessage = AppResources.SwapCameraError;
            await SpeechHelper.TrySpeechAsync(StatusMessage);
        }

        private string GetNormalizedMessage(string message)
            => message.Replace(Constants.SentenceEnd, ". ").TrimEnd('.').Trim().Replace("  ", " ").Replace(" .", ".").Replace("..", ".");
    }
}
