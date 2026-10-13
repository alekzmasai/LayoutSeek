#include "TestShell.cpp"
#include <cstdio>
int wmain(int argc,wchar_t** argv) {
    RECT rect{}; HWND window=nullptr;
    int start=argc==2 && wcscmp(argv[1],L"start")==0;
    HRESULT hr=LauncherRectangle(start,&rect,&window);
    if(FAILED(hr)){std::fprintf(stderr,"UIA taskbar discovery HRESULT=%08x\n",static_cast<unsigned>(hr));return 1;}
    std::printf("%lld %ld %ld %ld %ld\n",reinterpret_cast<long long>(window),rect.left,rect.top,rect.right,rect.bottom);return 0;
}
