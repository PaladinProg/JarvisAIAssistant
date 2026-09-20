import io
import torch
import soundfile as sf
from fastapi import FastAPI, Response
import uvicorn
import re
from transliterate import translit

app = FastAPI()

device = torch.device('cpu')
torch.set_num_threads(4)
model, _ = torch.hub.load(repo_or_dir='snakers4/silero-models',
                          model='silero_tts',
                          language='ru',
                          speaker='v4_ru',
                          trust_repo=True)
model.to(device)

def prepare_text(text: str) -> str:
    # Если в тексте есть английские буквы — транслитерируем их в кириллицу
    if re.search(r'[a-zA-Z]', text):
        try:
            text = translit(text, 'ru')
        except Exception:
            pass
    return text

@app.get("/tts")
def generate_speech(text: str, speaker: str = "aidar"):
    clean_text = prepare_text(text)
    audio = model.apply_tts(text=clean_text, speaker=speaker, sample_rate=48000)
    
    buffer = io.BytesIO()
    sf.write(buffer, audio.numpy(), 48000, format='WAV')
    return Response(content=buffer.getvalue(), media_type="audio/wav")

if __name__ == "__main__":
    print("\n>>> Silero TTS запущен <<<")
    uvicorn.run(app, host="127.0.0.1", port=8008, log_level="warning")