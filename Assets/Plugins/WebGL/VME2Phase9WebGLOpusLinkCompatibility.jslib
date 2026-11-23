mergeInto(LibraryManager.library, {
    // این تابع فقط ساخت کدگذار قدیمی را برای آزمون مسیر ارتباطی شبیه سازی می کند.
    opus_encoder_create: function(sampleRate, channels, application, errorPtr) {
        if (errorPtr) HEAP32[errorPtr >> 2] = 0;
        return 1;
    },

    // این تابع آزادسازی کدگذار شبیه سازی شده را بدون عملیات واقعی می پذیرد.
    opus_encoder_destroy: function(encoder) {
    },

    // این تابع در این فاز داده صوتی واقعی تولید نمی کند.
    opus_encode_float: function(encoder, pcmPtr, frameSize, dataPtr, maxDataBytes) {
        return 0;
    },

    // این تابع تنظیمات کدگذار شبیه سازی شده را برای سازگاری قبول می کند.
    opus_encoder_ctl: function(encoder, request, value) {
        return 0;
    },

    // این تابع فقط ساخت رمزگشای قدیمی را برای آزمون مسیر ارتباطی شبیه سازی می کند.
    opus_decoder_create: function(sampleRate, channels, errorPtr) {
        if (errorPtr) HEAP32[errorPtr >> 2] = 0;
        return 2;
    },

    // این تابع آزادسازی رمزگشای شبیه سازی شده را بدون عملیات واقعی می پذیرد.
    opus_decoder_destroy: function(decoder) {
    },

    // این تابع در صورت فراخوانی خروجی سکوت با اندازه درخواستی تولید می کند.
    opus_decode_float: function(decoder, dataPtr, length, pcmPtr, frameSize, decodeFec) {
        if (pcmPtr && frameSize > 0) {
            var start = pcmPtr >> 2;
            for (var index = 0; index < frameSize; index += 1) {
                HEAPF32[start + index] = 0;
            }
        }
        return frameSize;
    }
});
