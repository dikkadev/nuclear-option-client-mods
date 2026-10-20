using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearDeathBlackout
{
    internal sealed class DeathBlackoutController : MonoBehaviour
    {
        private static DeathBlackoutController? _instance;

        private CanvasGroup? _canvasGroup;
        private Coroutine? _blackoutRoutine;
        private bool _audioMuted;
        private float _savedAudioVolume = 1f;

        internal static void EnsureCreated()
        {
            if (_instance != null)
            {
                return;
            }

            var controllerObject = new GameObject("NuclearDeathBlackout_Controller");
            DontDestroyOnLoad(controllerObject);
            _instance = controllerObject.AddComponent<DeathBlackoutController>();
            _instance.EnsureOverlay();
        }

        internal static void DestroyInstance()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.RestoreAudioNow();
            Destroy(_instance.gameObject);
            _instance = null;
        }

        internal static void PlayBlackout()
        {
            EnsureCreated();
            _instance?.PlayBlackoutInternal();
        }

        internal static void CancelBlackout()
        {
            if (_instance != null) _instance.CancelInternal();
        }

        private void PlayBlackoutInternal()
        {
            if (!Plugin.Instance.Enabled.Value)
            {
                return;
            }

            if (_blackoutRoutine != null)
            {
                StopCoroutine(_blackoutRoutine);
                _blackoutRoutine = null;
                RestoreAudioNow();
            }

            _blackoutRoutine = StartCoroutine(BlackoutRoutine());
        }

        private IEnumerator BlackoutRoutine()
        {
            EnsureOverlay();
            if (_canvasGroup == null)
            {
                _blackoutRoutine = null;
                yield break;
            }

            _canvasGroup.gameObject.SetActive(true);
            _canvasGroup.transform.SetAsLastSibling();

            var muteAudio = Plugin.Instance.MuteAudio.Value;
            if (muteAudio)
            {
                _savedAudioVolume = AudioListener.volume;
                _audioMuted = true;
                AudioListener.volume = 0f;
            }

            yield return FadeCanvas(0f, 1f, Plugin.Instance.FadeInSeconds.Value);
            _canvasGroup.alpha = 1f;

            yield return WaitUnscaled(Plugin.Instance.BlackoutSeconds.Value);

            var fadeOutSeconds = Mathf.Max(0f, Plugin.Instance.FadeOutSeconds.Value);
            var restoreSeconds = muteAudio ? Mathf.Max(0f, Plugin.Instance.AudioRestoreSeconds.Value) : 0f;
            var transitionSeconds = Mathf.Max(fadeOutSeconds, restoreSeconds);

            if (transitionSeconds <= 0f)
            {
                _canvasGroup.alpha = 0f;
                RestoreAudioNow();
            }
            else
            {
                var elapsed = 0f;
                while (elapsed < transitionSeconds)
                {
                    elapsed += Time.unscaledDeltaTime;

                    if (fadeOutSeconds <= 0f)
                    {
                        _canvasGroup.alpha = 0f;
                    }
                    else
                    {
                        _canvasGroup.alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(elapsed / fadeOutSeconds));
                    }

                    if (muteAudio)
                    {
                        if (restoreSeconds <= 0f)
                        {
                            RestoreAudioNow();
                        }
                        else
                        {
                            AudioListener.volume = Mathf.Lerp(0f, _savedAudioVolume, Mathf.Clamp01(elapsed / restoreSeconds));
                        }
                    }

                    yield return null;
                }

                _canvasGroup.alpha = 0f;
                RestoreAudioNow();
            }

            _canvasGroup.gameObject.SetActive(false);
            _blackoutRoutine = null;
        }

        private IEnumerator FadeCanvas(float fromAlpha, float toAlpha, float seconds)
        {
            if (_canvasGroup == null)
            {
                yield break;
            }

            seconds = Mathf.Max(0f, seconds);
            if (seconds <= 0f)
            {
                _canvasGroup.alpha = toAlpha;
                yield break;
            }

            var elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                _canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, Mathf.Clamp01(elapsed / seconds));
                yield return null;
            }

            _canvasGroup.alpha = toAlpha;
        }

        private static IEnumerator WaitUnscaled(float seconds)
        {
            seconds = Mathf.Max(0f, seconds);
            var elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private void EnsureOverlay()
        {
            if (_canvasGroup != null)
            {
                return;
            }

            var canvasObject = new GameObject("NuclearDeathBlackout_Overlay", typeof(RectTransform));
            canvasObject.transform.SetParent(transform, false);

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            canvasObject.AddComponent<CanvasScaler>();
            _canvasGroup = canvasObject.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;

            var imageObject = new GameObject("Blackout", typeof(RectTransform));
            imageObject.transform.SetParent(canvasObject.transform, false);
            var rectTransform = (RectTransform)imageObject.transform;
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;

            var image = imageObject.AddComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = false;

            canvasObject.SetActive(false);
        }

        private void RestoreAudioNow()
        {
            if (!_audioMuted)
            {
                return;
            }

            AudioListener.volume = _savedAudioVolume;
            _audioMuted = false;
        }

        private void OnDisable()
        {
            CancelInternal();
        }

        private void CancelInternal()
        {
            StopAllCoroutines();
            _blackoutRoutine = null;
            RestoreAudioNow();
            if (_canvasGroup != null)
            {
                _canvasGroup.alpha = 0f;
                _canvasGroup.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            RestoreAudioNow();
            if (_instance == this) _instance = null;
        }
    }
}
